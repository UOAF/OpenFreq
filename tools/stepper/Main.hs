import Control.Exception
import Control.Monad
import Data.Attoparsec.ByteString (IResult(..), parseWith)
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

data Options = Options
    { input :: Maybe FilePath
    , grace :: Milli
    , ownshipName :: String
    }
    deriving stock (Show)

options :: Parser Options
options = Options <$> optional pinp <*> pgrace <*> pownship where
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
    let inBracket go = case opts.input of
            Just fp -> withFile fp ReadMode $ \fh -> go fh
            Nothing -> go stdin
    inBracket (run opts.grace opts.ownshipName)

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

-- | When did a player talk? When did they step on others?
-- These are (start, end) times, which we merge when we show them,
-- so that talking on two frequencies at once counts once.
-- (Someone keying several radios at once will generate several PTT downs in sequence.)
data PlayerStats = PlayerStats {
    talks :: ![(Milli, Milli)],
    steps :: ![(Milli, Milli)]
}

instance Semigroup PlayerStats where
    l <> r = PlayerStats (l.talks <> r.talks) (l.steps <> r.steps)

instance Monoid PlayerStats where
    mempty = PlayerStats [] []

-- | Tracked as we fold the log - per-frequency stats (and who's talking) and per-player stats.
data MissionState = MissionState {
    frequencies :: !(Word64Map FrequencyState),
    playerStats :: !(HashMap Speaker PlayerStats)
    }

data FrequencyState = FrequencyState {
    heterodyneState :: HeterodyneState,
    heterodyneSum :: !Milli,
    speakers :: !(HashMap Speaker SpeakerState)
    }

-- | Nobody is stepping on others on this frequency, or when that started.
data HeterodyneState = NoStep | Stepping !Milli

data SpeakerState = SpeakerState {
    start :: !Milli,
    gameTime :: !(Maybe Text),
    step :: !StepVerdict
    }

-- | Did this PTT step on anyone?
data StepVerdict
    -- | No. Nobody else was talking, or they started less than the grace period before us.
    = Clean
    -- | We don't know yet. We started more than the grace period after these folks.
    -- It's a step if we overlap any of them for more than the grace period.
    | Pending !(HashSet Speaker)
    | Stepped

process :: Milli -> String -> [LogLine] -> IO ()
process grace ownship ls = do
    res <- foldM (accEvent grace ownship) (MissionState mempty mempty) (inGamePtts ls)
    showStats grace ownship res

-- | What we fold into our mission state.
data Event = Heard PttLine | Restart

-- Keep only the PTTs that in-game (3D) players hear, and the restarts that end all PTTs.
-- For a client log, this is non-lobby comm while we were in-game, and our own PTTs from that time.
-- For a server log, this is every PTT from an in-game player, on every frequency.
inGamePtts :: [LogLine] -> [Event]
inGamePtts = go InLobby HS.empty where
    go _ _ [] = []
    -- Assume clients always start in-lobby, so wait for a transition to game mode.
    -- A restart also ends the PTTs we ignored.
    go _ _ (AppStart : ls) = Restart : go InLobby HS.empty ls
    go _ dropped (ModeChange mode : ls) = go mode dropped ls
    -- `dropped` is tracking PTT starts we ignored so that we can ignore the PTT end too.
    go mode !dropped (Ptt l : ls) = case l.edge of
        PttStart
            -- Out of caution, remove any previously ignored PTT start that had no end
            -- (e.g., someone crashes while talking in the lobby, then reconnects.)
            -- so that we don't ignore a PTT end for this start (which we heard!)
            | heard -> Heard l : go mode (HS.delete k dropped) ls
            | otherwise -> go mode (HS.insert k dropped) ls
        PttEnd
            | HS.member k dropped -> go mode (HS.delete k dropped) ls
            | otherwise -> Heard l : go mode dropped ls
      where
        k = (l.who, l.frequency)
        -- On server logs, all comm is marked as InLobby or InGame.
        -- On client logs, our own aren't (so track when we enter game mode).
        heard = (l.source == Server || mode == InGame) && l.gameMode /= Just InLobby

accEvent :: Milli -> String -> MissionState -> Event -> IO MissionState
accEvent grace ownship ms (Heard l) = accTalk grace ownship ms l
-- A restart ends every PTT, but if the app crashed, it logged no PTT up for them.
-- We don't know how long those PTTs went, so we drop them (and their noises) instead of guessing.
accEvent _ ownship ms Restart = do
    forM_ (WM.toAscList ms.frequencies) $ \(freq, f) ->
        forM_ (HM.toList f.speakers) $ \(who, sstate) ->
            hPutStrLn stderr $ mconcat [
                "warning: PTT down from ",
                showSpeaker ownship who,
                " on ",
                showMHz freq,
                " at ",
                showGameTime sstate.gameTime,
                " had no PTT up before a restart"
                ]
    let endAll f = f{heterodyneState = NoStep, speakers = HM.empty}
    pure ms{frequencies = WM.map endAll ms.frequencies}

-- Fold each line ino our mission state, in IO so we can complain.
accTalk :: Milli -> String -> MissionState -> PttLine -> IO MissionState
accTalk grace ownship !ms l@PttLine{edge = PttStart} = do
    -- Nobody has talked on a new frequency yet.
    let newFrequency = FrequencyState NoStep 0 HM.empty
        f = fromMaybe newFrequency $ ms.frequencies WM.!? l.frequency
    -- Add our new speaker (determining if they might be stepping),
    newSpeakers <- pttOnFreq grace ownship f.speakers l
    -- Then see if the horrible noises started
    -- (if there's more than one speaker on freq now).
    let newHetState = case f.heterodyneState of
            NoStep | HM.size newSpeakers > 1 -> Stepping l.time
            steppin -> steppin
        newFreqState = f{heterodyneState = newHetState, speakers = newSpeakers}
    -- We don't chnage any player stats on PTT down
    pure $ ms{frequencies = WM.insert l.frequency newFreqState ms.frequencies}

accTalk grace ownship ms l@PttLine{edge = PttEnd} = case ms.frequencies WM.!? l.frequency of
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
                ps = PlayerStats [talk] [talk | stepped]
                newStats = HM.insertWith (<>) l.who ps ms.playerStats
                -- They're not speaking no more,
                -- so we know if anyone who started over them stepped on them.
                newSpeakers = HM.map (victimStopped grace l) $ HM.delete l.who f.speakers
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
                showSpeaker ownship l.who,
                " on ",
                showMHz l.frequency,
                ", but they weren't talking"
                ]
            pure ms
    Nothing -> do
        hPutStrLn stderr $ mconcat [
            "warning: PTT up from ",
            showSpeaker ownship l.who,
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

pttOnFreq :: Milli -> String -> HashMap Speaker SpeakerState -> PttLine -> IO (HashMap Speaker SpeakerState)
pttOnFreq grace ownship ss l = assert (l.edge == PttStart) $ do
    let -- We might be stepping on anyone who started talking > the grace period ago,
        -- if they keep talking for > the grace period after we started blabbing anyways.
        victims = HM.keysSet $ HM.filter (\s -> l.time - s.start > grace) ss
        verdict = if HS.null victims then Clean else Pending victims
    case ss HM.!? l.who of
        Nothing -> pure $ HM.insert l.who (SpeakerState l.time l.gameTime verdict) ss
        Just prev -> do
            hPutStrLn stderr $ mconcat [
                "warning: back-to-back PTT downs from ",
                showSpeaker ownship l.who,
                " on ",
                showMHz l.frequency,
                " at ",
                showGameTime prev.gameTime,
                " and ",
                showGameTime l.gameTime
                ]
            pure ss

showSpeaker :: String -> Speaker -> String
showSpeaker ownship Own = ownship
showSpeaker _ (Named n) = T.unpack n

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

showStats :: Milli -> String -> MissionState -> IO ()
showStats grace ownship m = do
    let talkTime = sum $ unionLength . (.talks) <$> HM.elems m.playerStats
    if talkTime > 0
        then showStats' grace ownship m
        else putStrLn "Nobody said a thing? Is this thing on?"

showStats' :: Milli -> String -> MissionState -> IO ()
showStats' grace ownship m = do
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
                "  ", showSpeaker ownship who, ": ", showDuration t,
                " (", show n, if n == 1 then " time)" else " times)"
                ]
    unless noSteps $ do
        putStrLn $ "\nA " <> showDuration grace <> " grace period was given for folks who started at almost the same time,"
        putStrLn "and for folks who started just before the others stopped."
    unless (null steppers') $ do
        putStrLn "Your timer keeps counting even if the others stopped talking first."
        putStrLn "(You didn't know that, you were busy yapping!)"

    let yappers = ranked $ second (unionLength . (.talks)) <$> ps
    unless (null yappers) $ do
        putStrLn "\nYappers:"
        forM_ yappers $ \(who, t) ->
            putStrLn $ "  " <> showSpeaker ownship who <> ": " <> showDuration t

-- | Drop the zeros, and put the biggest first.
ranked :: [(k, Milli)] -> [(k, Milli)]
ranked = sortOn (Down . snd) . filter ((> 0) . snd)
