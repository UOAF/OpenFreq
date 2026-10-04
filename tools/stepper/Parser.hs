module Parser where

import Control.Applicative ((<|>), optional)
import Control.Monad
import Data.Attoparsec.ByteString.Char8
import Data.ByteString (ByteString)
import Data.ByteString qualified as BS
import Data.Bifunctor (bimap)
import Data.ByteString.Char8 qualified as BC
import Data.Either (isRight)
import Data.Fixed
import Data.Maybe (fromMaybe)
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding (decodeUtf8Lenient)
import Data.Word

-- | Read the start line of a session log, then fold over the other lines that we use,
-- getting more input from `refill` as needed.
-- Stop with an error if the input is not a session log, or if it has a line that we use but cannot read.
foldLog :: IO ByteString -> (StartLine -> IO s) -> (s -> LogLine -> IO s) -> IO (Either String s)
foldLog refill begin step = nextLine refill BS.empty >>= \case
    Nothing -> pure $ Left "the input is empty, so it is not a session log"
    Just (l, rest) -> case parseOnly (prefix *> startMessage) l of
        Left _ -> pure . Left $ mconcat [
            "line 1 is not an OpenFreq start line, so the input is not a session log:\n  ",
            showLine l
            ]
        Right start -> begin start >>= go (2 :: Int) rest
  where
    go !n input !s = nextLine refill input >>= \case
        Nothing -> pure $ Right s
        Just (l, rest) -> case readLine l of
            Right Nothing -> go (n + 1) rest s
            Right (Just ll) -> step s ll >>= go (n + 1) rest
            Left problem -> pure . Left $ mconcat [
                "line ", show n, problemText, ":\n  ", showLine l, hint
                ]
              where
                (problemText, hint) = case problem of
                    BadPtt -> (" is a PTT line that stepper cannot read", "")
                    SecondStart -> (" is a second start line",
                        "\nA session log has one start line. Did someone join two logs?")

showLine :: ByteString -> String
showLine = T.unpack . decodeUtf8Lenient

-- | The next line and the input after it, or Nothing at the end of the input.
-- Attoparsec keeps all the input of a parse buffered in case of backtracking,
-- so we take one line at a time.
nextLine :: IO ByteString -> ByteString -> IO (Maybe (ByteString, ByteString))
nextLine refill input
    -- Between lines, get more input ourselves to find the end of the log.
    -- (At the end of the input, `line` matches an empty line forever.)
    | BS.null input = do
        chunk <- refill
        if BS.null chunk then pure Nothing else fromChunk chunk
    | otherwise = fromChunk input
  where
    fromChunk i = parseWith refill line i >>= \case
        Done rest l -> pure $ Just (l, rest)
        _ -> error "absurd: a line always parses"
    -- The last line of a log that is still being written can have no line ending.
    line = do
        l <- takeTill (== '\n') <* (void (char '\n') <|> endOfInput)
        pure . fromMaybe l $ BS.stripSuffix "\r" l

data LogLine = Ptt PttLine | ModeChange GameMode

data Problem = BadPtt | SecondStart

-- | Read one line. Lines that we do not use are Nothing.
readLine :: ByteString -> Either Problem (Maybe LogLine)
readLine l = case parseOnly ((,) <$> prefix <*> takeByteString) l of
    -- For example, a line of a stack trace.
    Left _ -> Right Nothing
    Right (t, msg)
        | Just body <- BS.stripPrefix "PTT start: " msg -> ptt t PttStart body
        | Just body <- BS.stripPrefix "PTT end: " msg -> ptt t PttEnd body
        | Just body <- BS.stripPrefix "Game mode changed to " msg ->
            Right . either (const Nothing) (Just . ModeChange) $ parseOnly (gameMode <* endOfInput) body
        | isRight (parseOnly startMessage msg) -> Left SecondStart
        | otherwise -> Right Nothing
  where
    ptt t edge body = bimap (const BadPtt) (Just . Ptt) $ parseOnly (pttLine t edge) body

-- | The start of each line: the seconds since the app started, the level, and the source context.
-- ex: 8025.123 [INF] [OpenFreqClient.Services.OpenFreqService] PTT start: ...
prefix :: Parser Milli
prefix = do
    seconds <- rational @Rational
    _ <- string " ["
    _ <- count 3 . satisfy $ inClass "A-Z"
    _ <- string "] ["
    _ <- takeTill (== ']')
    _ <- string "] "
    pure $ fromRational seconds

data Source = Client | Server
    deriving stock (Show, Eq)

data StartLine = StartLine {
    source :: !Source,
    version :: !Text,
    -- | When the app started, like 2026-09-26 14:21:20 -04:00
    wallTime :: !Text
    }

-- ex: OpenFreq Client 1.1.4 starting at 2026-09-26T14:21:20.0650000-04:00
startMessage :: Parser StartLine
startMessage = do
    _ <- string "OpenFreq "
    src <- (Client <$ string "Client") <|> (Server <$ string "Server")
    ver <- char ' ' *> takeWhile1 (/= ' ')
    wall <- string " starting at " *> isoTime
    endOfInput
    pure $ StartLine src (decodeUtf8Lenient ver) wall

-- | An ISO 8601 time with a UTC offset, without the fraction of a second.
-- ex: 2026-09-26T14:21:20.0650000-04:00 gives 2026-09-26 14:21:20 -04:00
isoTime :: Parser Text
isoTime = do
    let digits n = BC.pack <$> count n digit
        dashed = BS.intercalate "-" <$> sequence [digits 4, char '-' *> digits 2, char '-' *> digits 2]
        coloned = BS.intercalate ":" <$> sequence [digits 2, char ':' *> digits 2, char ':' *> digits 2]
    date <- dashed
    time <- char 'T' *> coloned
    _ <- optional $ char '.' *> takeWhile1 isDigit
    offset <- string "Z" <|> do
        sign <- string "+" <|> string "-"
        hh <- digits 2
        mm <- char ':' *> digits 2
        pure $ mconcat [sign, hh, ":", mm]
    pure . decodeUtf8Lenient $ BS.intercalate " " [date, time, offset]

data PttEdge = PttStart | PttEnd
    deriving stock (Show, Eq)

data GameMode = InLobby | InGame
    deriving stock (Show, Eq)

data PttLine = PttLine {
    time :: !Milli,
    edge :: !PttEdge,
    who :: !Text,
    frequency :: !Word64,
    gameMode :: Maybe GameMode,
    gameTime :: Maybe Text
    }

-- | The rest of a PTT line, after "PTT start: " or "PTT end: ".
-- ex: Turcu (34092373-30da-487a-b58e-8c6de88466a8) on 139.700 MHz, 3D, game time 01:01:10
pttLine :: Milli -> PttEdge -> Parser PttLine
pttLine t edge = do
    let hexDigits n = void . count n . satisfy $ inClass "0-9a-fA-F"
        -- IDs are GUIDs, like 34092373-30da-487a-b58e-8c6de88466a8.
        -- Match that whole shape so that we don't cut a name like "Bob (ace)" short.
        parensUid = do
            _ <- string " ("
            hexDigits 8
            forM_ [4, 4, 4, 12] $ \n -> char '-' *> hexDigits n
            void $ char ')'
    spk <- decodeUtf8Lenient . BC.pack <$> manyTill anyChar parensUid
    freq <- string " on " *> frequency
    -- Lines have no game time when the client has no game clock.
    let gameTimeEnd = optional (string ", game time " *> gameTime) <* endOfInput
    (mode, gt) <- case edge of
        -- Start lines from other players have a mode. Our own lines don't.
        PttStart -> do
            mode <- optional $ string ", " *> gameModeDims
            (mode,) <$> gameTimeEnd
        -- End lines from other players have a reason (", released"), which is free text.
        -- Our own lines don't.
        PttEnd -> (Nothing,) <$> (gameTimeEnd <|> (string ", " *> skipTill gameTimeEnd))
    pure $ PttLine t edge spk freq mode gt
  where
    gameModeDims = (InLobby <$ string "2D") <|> (InGame <$ string "3D")

-- ex: In-game
-- BMS mode logs this when the flying state changes. GCI mode logs it when the user changes the mode.
gameMode :: Parser GameMode
gameMode = (InGame <$ string "In-game") <|> (InLobby <$ string "Lobby")

frequency :: Parser Word64
frequency = do
    freq <- rational @Rational
    _ <- string " MHz"
    pure . truncate $ freq * 1_000_000

gameTime :: Parser Text
gameTime = do
    s <- sequence [digit, digit, char ':', digit, digit, char ':', digit, digit]
    pure $ T.pack s

-- | Skip ahead until `end` parses.
skipTill :: Parser a -> Parser a
skipTill end = go where go = end <|> (anyChar *> go)
