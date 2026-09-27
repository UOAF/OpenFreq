import Control.Exception
import Control.Monad
import Data.Attoparsec.ByteString hiding (take)
import Data.Attoparsec.ByteString.Char8 hiding (inClass, takeTill, satisfy, skipWhile, take)
import Data.Attoparsec.Combinator
import Data.ByteString (ByteString)
import Data.ByteString qualified as BS
import Data.Bifunctor
import Data.Fixed
import Data.Hashable
import Data.HashMap.Strict (HashMap)
import Data.HashMap.Strict qualified as HM
import Data.HashSet qualified as HS
import Data.List (intersperse, sort, sortOn)
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding (decodeUtf8)
import Data.Maybe
import Data.Ratio
import Data.Word
import GHC.Data.Word64Map.Strict (Word64Map)
import GHC.Data.Word64Map.Strict qualified as WM
import GHC.Generics
import Options.Applicative hiding (Parser, option)
import Options.Applicative qualified as Args
import System.Exit (exitFailure)
import System.IO (Handle, IOMode(..), hPutStrLn, stdin, stderr, withFile)
import Data.Ord (Down(..))

data Options = Options
    { optInput :: Maybe FilePath
    , optGrace :: Milli
    , optOwnship :: String
    }
    deriving stock (Show)

options :: Args.Parser Options
options = Options <$> optional pinp <*> pgrace <*> pownship where
    pinp = strArgument $ mconcat [
        metavar "FILE",
        help "Input file, defaults to stdin"
        ]
    pgrace = fmap MkFixed $ Args.option auto $ mconcat [
        long "grace",
        short 'g',
        metavar "MS",
        value 500,
        showDefault,
        help "Grace period in milliseconds"
        ]
    pownship = strOption $ mconcat [
        long "ownship",
        metavar "NAME",
        value "You",
        showDefault,
        help "Name to show for your own radio"
        ]

parseOptions :: IO Options
parseOptions = execParser parser where
    parser = info (options <**> helper) $
        fullDesc <> progDesc "Who keeps talking over people?"


main :: IO ()
main = do
    opts <- parseOptions
    let inBracket go = case optInput opts of
            Just fp -> withFile fp ReadMode $ \fh -> go fh
            Nothing -> go stdin
    inBracket (run (optGrace opts) (optOwnship opts))

run :: Milli -> String -> Handle -> IO ()
run grace ownship fh = do
    let refill = BS.hGet fh $ 1024 * 1024
    res <- parseWith refill parseLog =<< refill
    case res of
        Fail _ ctxs msg -> do
            mapM_ (\c -> hPutStrLn stderr $ "in " <> c <> ":") ctxs
            hPutStrLn stderr msg
            exitFailure
        Done _ ls -> process grace ownship ls
        Partial _ -> error "absurd: partial result"

data PlayerStats = PlayerStats {
    psTalkTime :: !Milli,
    psStepTime :: !Milli
}

instance Semigroup PlayerStats where
    l <> r = PlayerStats (psTalkTime l + psTalkTime r) (psStepTime l + psStepTime r)

instance Monoid PlayerStats where
    mempty = PlayerStats 0 0

data MissionState = MissionState {
    msFrequencies :: !(Word64Map FrequencyState),
    msPlayerStats :: !(HashMap Speaker PlayerStats)
    }

data FrequencyState = FrequencyState {
    fsHeterodyneState :: HeterodyneState,
    fsHeterodyneSum :: !Milli,
    fsSpeakers :: !(HashMap Speaker SpeakerState)
    }

data HeterodyneState = NoStep | Stepping !Milli

data SpeakerState = SpeakerState {
    tsStart :: !Milli,
    tsGame :: !(Maybe Text),
    tsStep :: !Bool
    }

data Sortie = Sortie {
    srStart :: !Text,
    srPtts :: ![PttLine]
    }

process :: Milli -> String -> [LogLine] -> IO ()
process grace ownship ls = case sorties ls of
    [] -> putStrLn "Nobody sorties? Is this thing on?"
    ss -> sequence_ . intersperse (putStrLn "") $ showSortie <$> ss
  where
    showSortie s = do
        putStrLn $ "== Sortie at " <> T.unpack s.srStart <> " =="
        res <- foldM (accTalk grace ownship) (MissionState mempty mempty) s.srPtts
        showStats grace ownship res

-- Split the log into sorties, each from a change to in-game until the next mode change or client start.
-- Ignore PTT down (and up!) notifications for people in 2D/lobby mode, and all of them while we're in the lobby.
sorties :: [LogLine] -> [Sortie]
sorties = go Nothing HS.empty HS.empty where
    -- cur: the start of the sortie we're in (if we're in-game), and its PTT lines so far, newest first.
    -- dropped: (Speaker, Frequency) of PTT downs we dropped, so we drop their PTT up too.
    -- open: (Speaker, Frequency) whose PTT down we kept, which haven't ended yet.
    go cur _ _ [] = finish cur []
    -- A new client starts in the lobby, and the old one's PTTs never end.
    go cur _ _ (ClientStart : ls) = finish cur $ go Nothing HS.empty HS.empty ls
    -- A PTT that spans a mode change belongs to no sortie.
    go cur !dropped !open (ModeChange ts mode : ls) = finish cur $ go next (dropped <> open) HS.empty ls
      where next = if mode == InGame then Just (ts, []) else Nothing
    go cur dropped open (Ptt l : ls) = case (l.plEdge, cur) of
        (PttStart, Just (ts, ps))
            -- A 3D start means the next PTT end is theirs, even if a 2D start had no PTT end.
            | l.plGameMode /= Just InLobby -> go (Just (ts, l : ps)) (HS.delete k dropped) (HS.insert k open) ls
        (PttStart, _) -> go cur (HS.insert k dropped) (HS.delete k open) ls
        (PttEnd, Just (ts, ps))
            | not (HS.member k dropped) -> go (Just (ts, l : ps)) dropped (HS.delete k open) ls
        (PttEnd, _) -> go cur (HS.delete k dropped) (HS.delete k open) ls
      where k = (l.plWho, l.plFrequency)
    finish cur rest = maybe rest (\(ts, ps) -> Sortie ts (reverse ps) : rest) cur

-- Fold each line ino our mission state, in IO so we can complain.
accTalk :: Milli -> String -> MissionState -> PttLine -> IO MissionState
accTalk grace ownship !ms l@PttLine{plEdge = PttStart} = do
    let alterFreqs = \case
            -- Simple case: Nobody has talked on this frequency yet.
            -- They're clearly not stepping on anyone.
            Nothing -> pure . Just $ FrequencyState {
                fsHeterodyneState = NoStep,
                fsHeterodyneSum = 0,
                fsSpeakers = HM.singleton l.plWho $ SpeakerState l.plTime l.plGameTime False
                }
            Just f -> do
                -- Add our new speaker (determining if they're stepping),
                newSpeakers <- pttOnFreq grace ownship f.fsSpeakers l
                -- Then see if the horrible noises started
                -- (if there's more than one speaker on freq now).
                let newHetState = case f.fsHeterodyneState of
                        NoStep -> if HM.size newSpeakers > 1 then Stepping l.plTime else NoStep
                        steppin -> steppin
                pure $ Just f{fsHeterodyneState = newHetState, fsSpeakers = newSpeakers}
    newFreqs <- WM.alterF alterFreqs l.plFrequency ms.msFrequencies
    -- We don't chnage any player stats on PTT down
    pure $ ms{msFrequencies = newFreqs}

accTalk _ ownship ms l@PttLine{plEdge = PttEnd} = case ms.msFrequencies WM.!? l.plFrequency of
    Just f -> case f.fsSpeakers HM.!? l.plWho of
        Just sstate -> do
            -- We've found the frequency and the person who's been talking on it.
            -- See how long they've been yammering, and if we considered it stepping.
            let dt = l.plTime - sstate.tsStart
                ps = PlayerStats {
                    psTalkTime = dt,
                    psStepTime = if sstate.tsStep then dt else 0
                    }
                newStats = HM.insertWith (<>) l.plWho ps ms.msPlayerStats
                -- They're not speaking no more.
                newSpeakers = HM.delete l.plWho f.fsSpeakers :: HashMap Speaker SpeakerState
                -- Are the terrible noises over? And if so, how long were they going?
                (newHetState, stepToAdd) = case f.fsHeterodyneState of
                    NoStep -> (NoStep, 0)
                    Stepping since -> if HM.size newSpeakers > 1
                        then (Stepping since, 0) -- dear god it's still going
                        else (NoStep, assert (since < l.plTime) $ l.plTime - since)
                newFreqState = f{
                    fsHeterodyneState = newHetState,
                    fsHeterodyneSum = f.fsHeterodyneSum + stepToAdd,
                    fsSpeakers = newSpeakers
                    }
                newFreqs = WM.insert l.plFrequency newFreqState ms.msFrequencies :: Word64Map FrequencyState
            pure $ ms{msFrequencies = newFreqs, msPlayerStats = newStats}

        Nothing -> do
            hPutStrLn stderr $ mconcat [
                "warning: PTT up from ",
                showSpeaker ownship l.plWho,
                " on ",
                showMHz l.plFrequency,
                ", but they weren't talking"
                ]
            pure ms
    Nothing -> do
        hPutStrLn stderr $ mconcat [
            "warning: PTT up from ",
            showSpeaker ownship l.plWho,
            " on a frequency nobody has talked on (",
            showMHz l.plFrequency,
            ")"
            ]
        pure ms

pttOnFreq :: Milli -> String -> HashMap Speaker SpeakerState -> PttLine -> IO (HashMap Speaker SpeakerState)
pttOnFreq grace ownship ss l = assert (l.plEdge == PttStart) $ do
    let firstActiveSpeaker = listToMaybe . sort $ tsStart <$> HM.elems ss
        -- We're considered stepping if someone else started talking > the grace period ago
        -- and we started blabbing anyways.
        stepped = maybe False (\fas -> l.plTime - fas > grace) firstActiveSpeaker
    case ss HM.!? l.plWho of
        Nothing -> pure $ HM.insert l.plWho (SpeakerState l.plTime l.plGameTime stepped) ss
        Just prev -> do
            hPutStrLn stderr $ mconcat [
                "warning: back-to-back PTT downs from ",
                showSpeaker ownship l.plWho,
                " on ",
                showMHz l.plFrequency,
                " at ",
                showGameTime prev.tsGame,
                " and ",
                showGameTime l.plGameTime
                ]
            pure ss

showGameTime :: Maybe Text -> String
showGameTime = maybe "unknown game time" T.unpack

showMHz :: Word64 -> String
showMHz f = fstr <> " MHz" where
    fstr = showFixed True (realToFrac fmhz :: Milli)
    fmhz = fromIntegral f % 1_000_000

showDuration :: Milli -> String
showDuration m = showFixed True deci <> "s" where
    deci = fromIntegral (ceiling (m * 10)) / 10 :: Deci

showStats :: Milli -> String -> MissionState -> IO ()
showStats grace ownship m = do
    let talkTime = sum $ psTalkTime <$> HM.elems m.msPlayerStats
    if talkTime > 0
        then showStats' grace ownship m
        else putStrLn "Nobody said a thing? Is this thing on?"

showStats' :: Milli -> String -> MissionState -> IO ()
showStats' grace ownship m = do
    let totalHet = sum . fmap fsHeterodyneSum . WM.elems $ msFrequencies m
    if totalHet == 0
        then putStrLn "No steps, amazing job!"
        else  do
            putStrLn $ showDuration totalHet <> " of awful noises. By frequency:"
            let hetByFreq (f, h) = putStrLn $ "  " <> showMHz f <> ": " <> showDuration h
            let freqs = second fsHeterodyneSum <$> WM.toAscList m.msFrequencies
                freqs' = sortOn (Down . snd) $ filter (\s -> snd s > 0) freqs
            mapM_ hetByFreq freqs'

    let ps = HM.toList $ msPlayerStats m
        steppers = second psStepTime <$> ps
        steppers' = sortOn (Down . snd) $ filter (\s -> snd s > 0) steppers
    unless (null steppers') $ do
        putStrLn "\nSteppers:"
        forM_ steppers' $ \s ->
            putStrLn $ "  " <> showSpeaker ownship (fst s) <> ": " <> showDuration (snd s)
        putStrLn $ "\nA " <> showDuration grace <> " grace period was given for folks who started at almost the same time."
        putStrLn "Your timer keeps counting even if the others stopped talking first."
        putStrLn "(You didn't know that, you were busy yapping!)"

    let yappers = second psTalkTime <$> ps
        yappers' = sortOn (Down . snd) $ filter (\s -> snd s > 0) yappers
    unless (null yappers') $ do
        putStrLn "\nYappers:"
        forM_ yappers' $ \y ->
            putStrLn $ "  " <> showSpeaker ownship (fst y) <> ": " <> showDuration (snd y)


parseLog :: Parser [LogLine]
parseLog = catMaybes <$> manyTill maybeLogLine endOfInput

-- Skip ahead until end parses, but fail at the end of the line instead of looking at the next one.
skipInLine :: Parser a -> Parser a
skipInLine end = go
  where go = end <|> (satisfy (not . isEndOfLine) *> go)

maybeLogLine :: Parser (Maybe LogLine)
maybeLogLine = (Just <$> logLine) <|> (Nothing <$ takeLine)

-- ModeChange has the timestamp of its line, to label the sortie it starts.
data LogLine = Ptt PttLine | ModeChange Text GameMode | ClientStart

logLine :: Parser LogLine
logLine = (Ptt <$> pttLine) <|> modeLine <|> (ClientStart <$ startLine)

-- ex: 2026-09-26 15:09:25.093 -04:00 [INF] [OpenFreqClient.ViewModels.SettingsViewModel] Game mode changed to In-game
-- BMS mode logs this when the flying state changes. GCI mode logs it when the user changes the mode.
modeLine :: Parser LogLine
modeLine = do
    lineHas "Game mode changed to "
    ts <- manyTill (satisfy (not . isEndOfLine)) (string " [")
    _ <- skipInLine $ string "Game mode changed to "
    mode <- (InGame <$ string "In-game") <|> (InLobby <$ string "Lobby")
    endOfLine
    pure $ ModeChange (decodeUtf8 $ BS.pack ts) mode

-- ex: 2026-09-26 15:52:39.273 -04:00 [INF] [] OpenFreq Client 1.1.4 starting
startLine :: Parser ()
startLine = do
    lineHas "] OpenFreq Client "
    skipInLine $ string " starting" *> endOfLine

data PttEdge = PttStart | PttEnd
    deriving stock (Show, Eq)

data Speaker = Own | Named Text
    deriving stock (Eq, Generic)
    deriving anyclass (Hashable)

showSpeaker :: String -> Speaker -> String
showSpeaker ownship Own = ownship
showSpeaker _ (Named n) = T.unpack n

data GameMode = InLobby | InGame
    deriving stock (Show, Eq)

data PttLine = PttLine {
    plTime :: !Milli,
    plEdge :: !PttEdge,
    plWho :: !Speaker,
    plFrequency :: !Word64,
    plGameMode :: Maybe GameMode,
    plGameTime :: !(Maybe Text)
    }

-- ex: 2026-09-19 13:11:39.653 -07:00 [INF] [OpenFreqClient.Services.OpenFreqService] PTT start: Turcu (34092373-30da-487a-b58e-8c6de88466a8) on 139.700 MHz, 3D, game time 01:01:10
pttLine :: Parser PttLine
pttLine = do
    lineHas "PTT "
    -- Skip yyyy-mm-dd
    _ <- skipInLine space
    hh <- twoDigit
    _ <- char8 ':'
    mm <- twoDigit
    _ <- char8 ':'
    ss <- rational @Rational
    let h = fromIntegral (60 * 60 * hh) :: Milli
        m = fromIntegral (60 * mm) :: Milli
        s = realToFrac ss :: Milli
        t = h + m + s
    edge <- skipInLine $ (PttStart <$ string "PTT start: ") <|> (PttEnd <$ string "PTT end: ")
    let parensUid = do
            _ <- string " ("
            skipWhile $ inClass "0-9a-fA-F-"
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
    pure $ PttLine t edge spk freq mode gt

twoDigit :: Parser Integer
twoDigit = read <$> sequence [digit, digit]

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
