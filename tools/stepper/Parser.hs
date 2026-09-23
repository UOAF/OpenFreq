module Parser where

import Control.Applicative ((<|>), optional)
import Control.Monad
import Data.Attoparsec.ByteString hiding (take)
import Data.Attoparsec.ByteString.Char8 hiding (inClass, takeTill, satisfy, skipWhile, take)
import Data.Attoparsec.Combinator
import Data.ByteString (ByteString)
import Data.ByteString qualified as BS
import Data.Fixed
import Data.Hashable
import Data.Maybe
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding (decodeUtf8)
import Data.Word
import GHC.Generics

parseLog :: Parser [LogLine]
parseLog = catMaybes <$> manyTill maybeLogLine endOfInput

-- Skip ahead until end parses, but fail at the end of the line instead of looking at the next one.
skipInLine :: Parser a -> Parser a
skipInLine end = go
  where go = end <|> (satisfy (not . isEndOfLine) *> go)

maybeLogLine :: Parser (Maybe LogLine)
maybeLogLine = (Just <$> logLine) <|> (Nothing <$ takeLine)

data LogLine = Ptt PttLine | ModeChange GameMode | AppStart

logLine :: Parser LogLine
logLine = (Ptt <$> pttLine) <|> (ModeChange <$> modeLine) <|> (AppStart <$ startLine)

-- The time of day at the start of a line, without the date or UTC offset.
-- ex: 2026-09-19 13:11:39.653 -07:00
lineTime :: Parser Milli
lineTime = do
    -- Skip yyyy-mm-dd
    _ <- count 4 digit *> char8 '-' *> twoDigit *> char8 '-' *> twoDigit *> char8 ' '
    hh <- twoDigit
    _ <- char8 ':'
    mm <- twoDigit
    _ <- char8 ':'
    ss <- rational @Rational
    let h = fromIntegral (60 * 60 * hh) :: Milli
        m = fromIntegral (60 * mm) :: Milli
        s = realToFrac ss :: Milli
    pure $ h + m + s

-- ex: 2026-09-26 15:09:25.093 -04:00 [INF] [OpenFreqClient.ViewModels.SettingsViewModel] Game mode changed to In-game
-- BMS mode logs this when the flying state changes. GCI mode logs it when the user changes the mode.
modeLine :: Parser GameMode
modeLine = do
    lineHas "Game mode changed to "
    _ <- skipInLine $ string "Game mode changed to "
    mode <- (InGame <$ string "In-game") <|> (InLobby <$ string "Lobby")
    endOfLine
    pure mode

-- ex: 2026-09-26 15:52:39.273 -04:00 [INF] [] OpenFreq Client 1.1.4 starting
-- ex: 2026-09-26 20:11:27.224 +00:00 [INF] [] OpenFreq Server 1.1.4 starting
startLine :: Parser ()
startLine = do
    lineHas "OpenFreq Client" <|> lineHas "OpenFreq Server"
    skipInLine $ string "starting" *> endOfLine

data PttEdge = PttStart | PttEnd
    deriving stock (Show, Eq)

data Speaker = Own | Named Text
    deriving stock (Eq, Generic)
    deriving anyclass (Hashable)

data GameMode = InLobby | InGame
    deriving stock (Show, Eq)

data Source = Client | Server
    deriving stock (Eq)

data PttLine = PttLine {
    source :: !Source,
    time :: !Milli,
    edge :: !PttEdge,
    who :: !Speaker,
    frequency :: !Word64,
    gameMode :: Maybe GameMode,
    gameTime :: !(Maybe Text)
    }

-- ex: 2026-09-19 13:11:39.653 -07:00 [INF] [OpenFreqClient.Services.OpenFreqService] PTT start: Turcu (34092373-30da-487a-b58e-8c6de88466a8) on 139.700 MHz, 3D, game time 01:01:10
pttLine :: Parser PttLine
pttLine = do
    lineHas "PTT "
    t <- lineTime
    -- The server logs PTTs in the same format as clients log other players' PTTs.
    -- ex: 2026-09-26 19:10:59.306 +00:00 [INF] [OpenFreqServer.SignalingServer] PTT start: ...
    src <- option Client (Server <$ skipInLine (string "[OpenFreqServer."))
    edge <- skipInLine $ (PttStart <$ string "PTT start: ") <|> (PttEnd <$ string "PTT end: ")
    let hexDigits n = void . count n . satisfy $ inClass "0-9a-fA-F"
        -- IDs are GUIDs, like 34092373-30da-487a-b58e-8c6de88466a8.
        -- Match that whole shape so that we don't cut a name like "Bob (ace)" short.
        parensUid = do
            _ <- string " ("
            hexDigits 8
            forM_ [4, 4, 4, 12] $ \n -> char8 '-' *> hexDigits n
            _ <- char8 ')'
            pure ()
        namedSpeaker = manyTill (satisfy (not . isEndOfLine)) parensUid
    spk <- (Own <$ string "own radio ") <|> (Named . decodeUtf8 . BS.pack <$> namedSpeaker)
    freq <- skipInLine frequency
    -- Only PTT start lines from other players have a mode.
    -- PTT end lines and our own radio's lines don't.
    mode <- optional . skipInLine $ (InLobby <$ string "2D") <|> (InGame <$ string "3D")
    -- Lines have no game time when the client has no game clock.
    gt <- optional $ skipInLine gameTime
    -- Without a game time, nothing above consumes the end reason (", released").
    _ <- takeLine
    pure $ PttLine src t edge spk freq mode gt

twoDigit :: Parser Integer
twoDigit = read <$> count 2 digit

frequency :: Parser Word64
frequency = do
    freq <- rational @Rational
    _ <- string " MHz"
    pure . truncate $ freq * 1_000_000

gameTime :: Parser Text
gameTime = do
    _ <- string "game time "
    s <- sequence [digit, digit, char ':', digit, digit, char ':', digit, digit]
    pure $ T.pack s

lineHas :: ByteString -> Parser ()
lineHas t = do
    l <- lookAhead $ takeTill isEndOfLine
    guard $ t `BS.isInfixOf` l

-- The last line of a log that is still being written can have no line ending.
takeLine :: Parser ByteString
takeLine = takeTill isEndOfLine <* (endOfLine <|> endOfInput)
