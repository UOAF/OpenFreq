import Control.Exception
import Control.Monad (guard)
import Data.Attoparsec.ByteString
import Data.Attoparsec.ByteString qualified as A
import Data.Attoparsec.ByteString.Char8 hiding (inClass, takeTill, satisfy, skipWhile)
import Data.Attoparsec.Combinator
import Data.ByteString (ByteString)
import Data.ByteString qualified as BS
import Data.ByteString.Internal (c2w)
import Data.Fixed
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding (decodeUtf8)
import Data.Ratio
import Data.Maybe
import Options.Applicative hiding (Parser, option)
import Options.Applicative qualified as Args
import System.IO

data Options = Options
    { optInput :: Maybe FilePath
    , optGrace :: Int
    }
    deriving stock (Show)

options :: Args.Parser Options
options = Options <$> optional pinp <*> pgrace where
    pinp = strArgument $ mconcat [
        metavar "FILE",
        help "Input file. The default is stdin."
        ]
    pgrace = Args.option auto $ mconcat [
        long "grace",
        short 'g',
        metavar "MS",
        value 500,
        showDefault,
        help "Grace period in milliseconds"
        ]

parseOptions :: IO Options
parseOptions = execParser parser where
    parser = info (options <**> helper) $
        fullDesc <> progDesc "Who keeps talking over people?"


main :: IO ()
main = do
    opts <- parseOptions
    let inBracket go = case optInput opts of
            Just fp -> bracket (openFile fp ReadMode) hClose $ \fh -> go fh
            Nothing -> go stdin
    inBracket parseFile

parseFile :: Handle -> IO ()
parseFile fh = do
    let refill = BS.hGet fh $ 1024 * 1024
    res <- parseWith refill parsePtts =<< refill
    case res of
        Fail _ ctxs msg -> do
            mapM_ (\c -> putStrLn $ "in " <> c <> ":") ctxs
            hPutStrLn stderr msg
        Done _ ls -> mapM_ print ls
        Partial _ -> error "absurd: partial result"

parsePtts :: Parser [PttLine]
parsePtts = do
    _ <- skipTill (string "Flying state changed: false -> true" *> endOfLine)
    ptts <- manyTill maybePttLine (markerLine "Flying state changed: true -> false" <?> "3D end")
    pure $ catMaybes ptts

markerLine :: ByteString -> Parser ()
markerLine l = manyTill (satisfy (not . isEndOfLine)) (string l) *> endOfLine

-- Can a man get some Boyer-Moore?
skipTill :: Parser a -> Parser a
skipTill end = go
  where go = end <|> (anyChar *> go)

maybePttLine :: Parser (Maybe PttLine)
maybePttLine = (Just <$> pttLine) <|> (Nothing <$ takeLine)

data PttEdge = PttStart | PttEnd
    deriving stock (Show)

data Speaker = Own | Named Text
    deriving stock (Show)

data PttLine = PttLine {
    plTime :: !Milli,
    plEdge :: !PttEdge,
    plWho :: !Speaker
    }
    deriving stock (Show)

-- ex: 2026-09-19 13:11:39.653 -07:00 [INF] [OpenFreqClient.Services.OpenFreqService] PTT start: Turcu (34092373-30da-487a-b58e-8c6de88466a8) on 139.700 MHz, 3D, game time 01:01:10
pttLine :: Parser PttLine
pttLine = do
    lineHas "PTT "
    -- Skip yyyy-mm-dd
    _ <- skipTill space
    hh <- twoDigit
    _ <- word8 $ c2w ':'
    mm <- twoDigit
    _ <- word8 $ c2w ':'
    ss <- twoDigit
    _ <- word8 $ c2w '.'
    sss <- A.takeWhile $ inClass "0-9"
    let sss' = read @Integer (T.unpack (decodeUtf8 sss)) % (10 ^ BS.length (sss)) -- I'm sorry
    let h = fromIntegral (60 * 60 * hh) :: Milli
        m = fromIntegral (60 * mm) :: Milli
        s = fromIntegral ss :: Milli
        subs = realToFrac sss' :: Milli
        t = h + m + s + subs
    _ <- skipTill (string "PTT ")
    edge <- (PttStart <$ string "start: ") <|> (PttEnd <$ string "end: ")
    let parensUid = do
            _ <- string " ("
            skipWhile $ inClass "0-9a-fA-F-"
            _ <- word8 (c2w ')')
            pure ()
        namedSpeaker = manyTill (satisfy (not . isEndOfLine)) parensUid
    spk <- (Own <$ string "own radio ") <|> (Named . decodeUtf8 . BS.pack <$> namedSpeaker)
    _ <- takeLine
    pure $ PttLine t edge $ spk

twoDigit :: Parser Integer
twoDigit = do
    d1 <- digit
    d2 <- digit
    pure $ read $ d1 : d2 : []

lineHas :: ByteString -> Parser ()
lineHas t = do
    l <- lookAhead $ takeTill isEndOfLine
    guard $ t `BS.isInfixOf` l

takeLine :: Parser ByteString
takeLine = takeTill isEndOfLine <* endOfLine
