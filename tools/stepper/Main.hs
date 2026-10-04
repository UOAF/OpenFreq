import Control.Exception
import Control.Monad
import Data.ByteString qualified as BS
import Data.Bifunctor
import Data.Fixed
import Data.HashMap.Strict (HashMap)
import Data.HashMap.Strict qualified as HM
import Data.HashSet (HashSet)
import Data.HashSet qualified as HS
import Data.List (sort, sortOn)
import Data.Maybe (fromMaybe)
import Data.Text (Text)
import Data.Text qualified as T
import Data.Ratio
import Data.Word
import GHC.Data.Word64Map.Strict (Word64Map)
import GHC.Data.Word64Map.Strict qualified as WM
import Options.Applicative
import System.Exit (exitFailure)
import System.IO (Handle, IOMode(..), hPutStrLn, stdin, stderr, withFile)
import Data.Ord (Down(..))

import Parser

data Options = Options {
    input :: Maybe FilePath ,
    grace :: Milli
    }
    deriving stock (Show)

options :: Parser Options
options = Options <$> optional pinp <*> pgrace where
    pinp = strArgument $ mconcat [
        metavar "FILE",
        help "Input file, defaults to stdin"
        ]
    pgrace = fmap MkFixed $ option auto $ mconcat [
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
    let inBracket go = case opts.input of
            Just fp -> withFile fp ReadMode $ \fh -> go fh
            Nothing -> go stdin
    inBracket (run opts.grace)

run :: Milli -> Handle -> IO ()
run grace fh = do
    let refill = BS.hGet fh $ 1024 * 1024
        -- Show which session the report is for.
        begin start = do
            putStrLn $ mconcat [
                "OpenFreq ", show start.source, " ", T.unpack start.version,
                ", started at ", T.unpack start.wallTime, "\n"
                ]
            pure $ ReadState start.source (HeardState InLobby HS.empty) (MissionState mempty mempty)
        -- Fold in each line that in-game players hear, as we read it.
        step (ReadState src heard ms) line = do
            let (heard', ptt) = inGamePtt src heard line
            ReadState src heard' <$> maybe (pure ms) (accTalk grace ms) ptt
    foldLog refill begin step >>= \case
        Left err -> do
            hPutStrLn stderr $ "error: " <> err
            exitFailure
        Right (ReadState _ _ ms) -> do
            warnUnended ms
            showStats grace ms

-- | What we track as we read the log.
data ReadState = ReadState !Source !HeardState !MissionState

-- | When did a player talk? When did they step on others? When did others talk over them?
-- These are (start, end) times, which we merge when we show them,
-- so that talking on two frequencies at once counts once.
-- (Someone keying several radios at once will generate several PTT downs in sequence.)
data PlayerStats = PlayerStats {
    talks :: [(Milli, Milli)],
    steps :: [(Milli, Milli)],
    -- | When others talked over this player, on a frequency where this player started talking first.
    -- No grace period unlike the other stats.
    -- (Even if it's nobody's fault, you still can't be heard!)
    steppedOn :: [(Milli, Milli)]
}

instance Semigroup PlayerStats where
    l <> r = PlayerStats (l.talks <> r.talks) (l.steps <> r.steps) (l.steppedOn <> r.steppedOn)

instance Monoid PlayerStats where
    mempty = PlayerStats [] [] []

-- | Tracked as we fold the log - per-frequency stats (and who's talking) and per-player stats.
data MissionState = MissionState {
    frequencies :: !(Word64Map FrequencyState),
    playerStats :: !(HashMap Text PlayerStats)
    }

data FrequencyState = FrequencyState {
    heterodyneState :: !HeterodyneState,
    heterodyneSum :: !Milli,
    speakers :: !(HashMap Text SpeakerState)
    }

-- | Nobody is stepping on others on this frequency, or when that started.
data HeterodyneState = NoStep | Stepping !Milli

data SpeakerState = SpeakerState {
    start :: !Milli,
    gameTime :: Maybe Text,
    step :: !StepVerdict,
    startedFirst :: !Bool
    }

-- | Did this PTT step on anyone?
data StepVerdict
    -- | No. Nobody else was talking, or they started less than the grace period before us.
    = Clean
    -- | We don't know yet. We started more than the grace period after these folks.
    -- It's a step if we overlap any of them for more than the grace period.
    | Pending !(HashSet Text)
    | Stepped

-- | What we need to know to decide which PTTs in-game players hear.
data HeardState = HeardState {
    mode :: !GameMode,
    -- | PTT starts we ignored, so that we can ignore the PTT end too.
    dropped :: !(HashSet (Text, Word64))
    }

-- Keep only the PTTs that in-game (3D) players hear.
-- For a client log, this is non-lobby comm while we were in-game, and our own PTTs from that time.
-- For a server log, this is every PTT from an in-game player, on every frequency.
inGamePtt :: Source -> HeardState -> LogLine -> (HeardState, Maybe PttLine)
-- Assume clients always start in-lobby, so wait for a transition to game mode.
inGamePtt _ hs (ModeChange mode) = (hs{mode = mode}, Nothing)
inGamePtt src hs (Ptt l) = case l.edge of
    PttStart
        -- Out of caution, remove any previously ignored PTT start that had no end
        -- (e.g., someone crashes while talking in the lobby, then reconnects.)
        -- so that we don't ignore a PTT end for this start (which we heard!)
        | heard -> (hs{dropped = HS.delete k hs.dropped}, Just l)
        | otherwise -> (hs{dropped = HS.insert k hs.dropped}, Nothing)
    PttEnd
        | HS.member k hs.dropped -> (hs{dropped = HS.delete k hs.dropped}, Nothing)
        | otherwise -> (hs, Just l)
  where
    k = (l.who, l.frequency)
    -- On server logs, all comm is marked as InLobby or InGame.
    -- On client logs, our own aren't (so track when we enter game mode).
    heard = (src == Server || hs.mode == InGame) && l.gameMode /= Just InLobby

-- | Warn about each PTT that is still open at the end of the log.
-- The app crashed, or the session is still running.
-- We don't know how long those PTTs went, so we drop them (and their noises) instead of guessing.
warnUnended :: MissionState -> IO ()
warnUnended ms =
    forM_ (WM.toAscList ms.frequencies) $ \(freq, f) ->
        forM_ (sortOn (\(_, s) -> s.start) $ HM.toList f.speakers) $ \(who, sstate) ->
            hPutStrLn stderr $ mconcat [
                "warning: PTT down from ",
                T.unpack who,
                " on ",
                showMHz freq,
                " at ",
                showGameTime sstate.gameTime,
                " had no PTT up before the log ended"
                ]

-- Fold each line ino our mission state, in IO so we can complain.
accTalk :: Milli -> MissionState -> PttLine -> IO MissionState
accTalk grace !ms l@PttLine{edge = PttStart} = do
    -- Nobody has talked on a new frequency yet.
    let newFrequency = FrequencyState NoStep 0 HM.empty
        f = fromMaybe newFrequency $ ms.frequencies WM.!? l.frequency
    -- Add our new speaker (determining if they might be stepping),
    newSpeakers <- pttOnFreq grace f.speakers l
    -- Then see if the horrible noises started
    -- (if there's more than one speaker on freq now).
    let newHetState = case f.heterodyneState of
            NoStep | HM.size newSpeakers > 1 -> Stepping l.time
            steppin -> steppin
        newFreqState = f{heterodyneState = newHetState, speakers = newSpeakers}
    -- We don't chnage any player stats on PTT down
    pure $ ms{frequencies = WM.insert l.frequency newFreqState ms.frequencies}

accTalk grace ms l@PttLine{edge = PttEnd} = case ms.frequencies WM.!? l.frequency of
    Just f -> case f.speakers HM.!? l.who of
        Just sstate -> do
            -- We've found the frequency and the person who's been talking on it.
            -- See how long they've been yammering, and if we considered it stepping.
            let talk = (sstate.start, l.time)
                stepped = case sstate.step of
                    Stepped -> True
                    -- The folks we started over are still talking, so we overlapped them this whole time.
                    Pending _ -> l.time - sstate.start > grace
                    Clean -> False
                others = HM.delete l.who f.speakers
                -- If we started first, anyone still talking who started after us has talked over us since they started.
                overUs = [(s.start, l.time) | sstate.startedFirst, s <- HM.elems others, s.start > sstate.start]
                -- If someone still talking started first, we have talked over them this whole time.
                overThem = [(who, PlayerStats [] [] [talk]) | (who, s) <- HM.toList others, s.startedFirst, s.start < sstate.start]
                ps = PlayerStats [talk] [talk | stepped] overUs
                newStats = foldr (uncurry $ HM.insertWith (<>)) ms.playerStats $ (l.who, ps) : overThem
                -- They're not speaking no more,
                -- so we know if anyone who started over them stepped on them.
                newSpeakers = HM.map (victimStopped grace l) others
                -- Are the terrible noises over? And if so, how long were they going?
                (newHetState, stepToAdd) = case f.heterodyneState of
                    NoStep -> (NoStep, 0)
                    Stepping since -> if HM.size newSpeakers > 1
                        then (Stepping since, 0) -- dear god it's still going
                        else (NoStep, assert (since <= l.time) $ l.time - since)
                newFreqState = f{
                    heterodyneState = newHetState,
                    heterodyneSum = f.heterodyneSum + stepToAdd,
                    speakers = newSpeakers
                    }
                newFreqs = WM.insert l.frequency newFreqState ms.frequencies
            pure $ ms{frequencies = newFreqs, playerStats = newStats}

        Nothing -> do
            hPutStrLn stderr $ mconcat [
                "warning: PTT up from ",
                T.unpack l.who,
                " on ",
                showMHz l.frequency,
                ", but they weren't talking"
                ]
            pure ms
    Nothing -> do
        hPutStrLn stderr $ mconcat [
            "warning: PTT up from ",
            T.unpack l.who,
            " on a frequency nobody has talked on (",
            showMHz l.frequency,
            ")"
            ]
        pure ms

-- | Someone stopped talking. Settle the verdict of a PTT that might have stepped on them.
victimStopped :: Milli -> PttLine -> SpeakerState -> SpeakerState
victimStopped grace l s = case s.step of
    Pending victims | HS.member l.who victims -> s{step = settle $ HS.delete l.who victims}
    _ -> s
  where
    settle rest
        -- We talked over them for more than the grace period.
        | l.time - s.start > grace = Stepped
        -- They stopped less than the grace period after we started, and so did the others we started over.
        | HS.null rest = Clean
        | otherwise = Pending rest

pttOnFreq :: Milli -> HashMap Text SpeakerState -> PttLine -> IO (HashMap Text SpeakerState)
pttOnFreq grace ss l = assert (l.edge == PttStart) $ do
    let -- We might be stepping on anyone who started talking > the grace period ago,
        -- if they keep talking for > the grace period after we started blabbing anyways.
        victims = HM.keysSet $ HM.filter (\s -> l.time - s.start > grace) ss
        verdict = if HS.null victims then Clean else Pending victims
    case ss HM.!? l.who of
        Nothing -> pure $ HM.insert l.who (SpeakerState l.time l.gameTime verdict (HM.null ss)) ss
        Just prev -> do
            hPutStrLn stderr $ mconcat [
                "warning: back-to-back PTT downs from ",
                T.unpack l.who,
                " on ",
                showMHz l.frequency,
                " at ",
                showGameTime prev.gameTime,
                " and ",
                showGameTime l.gameTime
                ]
            pure ss

showGameTime :: Maybe Text -> String
showGameTime = maybe "unknown game time" T.unpack

showMHz :: Word64 -> String
showMHz f = fstr <> " MHz" where
    fstr = showFixed True (realToFrac fmhz :: Milli)
    fmhz = fromIntegral f % 1_000_000

showDuration :: Milli -> String
showDuration m
    | deci > 60 = show mins <> "m " <> showSecs secs
    | otherwise = showSecs deci
  where
    deci = fromIntegral (ceiling (m * 10)) / 10 :: Deci
    (mins, secs) = deci `divMod'` 60 :: (Integer, Deci)
    showSecs s = showFixed True s <> "s"

-- | Merge overlapping (start, end) intervals, so that talking on two frequencies at once counts once.
mergeIntervals :: [(Milli, Milli)] -> [(Milli, Milli)]
mergeIntervals = go . sort where
    go ((s1, e1) : (s2, e2) : rest)
        | s2 <= e1 = go ((s1, max e1 e2) : rest)
        | otherwise = (s1, e1) : go ((s2, e2) : rest)
    go is = is

-- | The total time that some (start, end) intervals cover, counting overlaps once.
unionLength :: [(Milli, Milli)] -> Milli
unionLength = sum . fmap (\(s, e) -> e - s) . mergeIntervals

showStats :: Milli -> MissionState -> IO ()
showStats grace m = do
    let talkTime = sum $ unionLength . (.talks) <$> HM.elems m.playerStats
    if talkTime > 0
        then showStats' grace m
        else putStrLn "Nobody said a thing? Is this thing on?"

showStats' :: Milli -> MissionState -> IO ()
showStats' grace m = do
    let totalHet = sum . fmap (.heterodyneSum) . WM.elems $ m.frequencies
        ps = HM.toList m.playerStats
        -- How long each player stepped on others, and how many times.
        -- (Stepping on several frequencies at once, with several radios keyed, is one time.)
        stepStats steps = (unionLength steps, length $ mergeIntervals steps)
        steppers = second (stepStats . (.steps)) <$> ps
        steppers' = sortOn (Down . snd) $ filter (\s -> fst (snd s) > 0) steppers
        -- Noise from overlaps within the grace period has no steppers.
        noSteps = totalHet == 0 && null steppers'
    when noSteps $ putStrLn "No steps, amazing job!"
    when (totalHet > 0) $ do
        putStrLn $ showDuration totalHet <> " of awful noises. By frequency:"
        forM_ (ranked $ second (.heterodyneSum) <$> WM.toAscList m.frequencies) $ \(f, h) ->
            putStrLn $ "  " <> showMHz f <> ": " <> showDuration h

    unless (null steppers') $ do
        putStrLn $ (if totalHet > 0 then "\n" else "") <> "Steppers:"
        forM_ steppers' $ \(who, (t, n)) ->
            putStrLn $ mconcat [
                "  ", T.unpack who, ": ", showDuration t,
                " (", show n, if n == 1 then " time)" else " times)"
                ]
    unless noSteps $ do
        putStrLn $ "\nA " <> showDuration grace <> " grace period was given for folks who started at almost the same time,"
        putStrLn "and for folks who started just before the others stopped."
    unless (null steppers') $ do
        putStrLn "Your timer keeps counting even if the others stopped talking first."
        putStrLn "(You didn't know that, you were busy yapping!)"

    let steppees = ranked $ second (unionLength . (.steppedOn)) <$> ps
    unless (null steppees) $ do
        putStrLn "\nSteppees:"
        forM_ steppees $ \(who, t) ->
            putStrLn $ "  " <> T.unpack who <> ": " <> showDuration t

    let yappers = ranked $ second (unionLength . (.talks)) <$> ps
    unless (null yappers) $ do
        putStrLn "\nYappers:"
        forM_ yappers $ \(who, t) ->
            putStrLn $ "  " <> T.unpack who <> ": " <> showDuration t

-- | Drop the zeros, and put the biggest first.
ranked :: [(k, Milli)] -> [(k, Milli)]
ranked = sortOn (Down . snd) . filter ((> 0) . snd)
