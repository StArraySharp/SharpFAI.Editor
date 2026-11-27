using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{

// 时间轴：入口时刻、扫角、拍速换算。
// Timeline: entry times, sweeps, beat conversions.

    /// <summary>Fold an angle into [0, modulus).</summary>
    private static double Mod(double modulus, double value)
    {
        double result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    internal static int FloorAtTime(double[] entries, double time)
    {
        int first = 0, last = entries.Length - 1;
        while (first < last)
        {
            int middle = first + ((last - first + 1) >> 1);
            if (time >= entries[middle])
                first = middle;
            else
                last = middle - 1;
        }
        return first;
    }

    /// <summary>
    /// Computes the timeline on the **music file position** clock (CPU only, safe on a worker thread):
    ///   filePos(i) = entryTime[i] + offset - (separateCountdownTime ? countdownTicks * crotchet : 0)
    /// The music, the hitsound and every visual share this single clock, so they cannot drift apart.
    /// </summary>
    internal static PlaybackTimeline BuildTimeline(Level level, List<Floor> floors, IReadOnlyList<PlaybackEvent>? events = null,
        FloorCoreEvents? coreEvents = null, bool editor = false)
    {
        if (floors.Count == 0)
        {
            return new PlaybackTimeline();
        }

        // The chart clock includes the countdown and the actual initial sweep. The same timing
        // data carries Pause / Hold extra beats for the later sweeps.
        Dictionary<int, FloorTimingCalculator.SpeedContext>? speedContexts = editor ? [] : null;
        List<FloorTimingCalculator.FloorData> timing = speedContexts is not null
            ? FloorTimingCalculator.Calculate(level, coreEvents, speedContexts) : coreEvents is null
            ? FloorTimingCalculator.Calculate(level) : FloorTimingCalculator.Calculate(level, coreEvents);
        events ??= PlaybackEventBinder.BindForPlayback(level);
        PlanetTrackFloor[] planetTrack = PlanetTrackFloor.Resolve(timing, events);
        Dictionary<int, (EventEnums.AngleCorrectionDirection Direction, int Ticks)> pauseOptions = [];
        foreach (PauseAngleEvent pause in events.OfType<PauseAngleEvent>().Where(pause => pause.IsActive))
        {
            var previous = pauseOptions.GetValueOrDefault(pause.Floor, (EventEnums.AngleCorrectionDirection.Backward, 0));
            pauseOptions[pause.Floor] = (pause.Enabled("angleCorrectionDir", pause.AngleCorrectionDir) ?? previous.Item1,
                pause.Enabled("countdownTicks", pause.CountdownTicks) ?? 0);
        }

        double offsetSeconds = ReadLevelOffset(level);
        double pitch = ReadPlaybackPitch(level.root["settings"] as Newtonsoft.Json.Linq.JObject);

        double bpm = ReadSetting(level, "bpm", 0.0);
        if (!(bpm > 1e-6))
        {
            bpm = floors[0].bpm > 1e-6 ? floors[0].bpm : 120.0;
        }

        double crotchet = 60.0 / bpm;

        int countdownTicks = Math.Max(1, LevelPlaybackSettings.Setting(level, "countdownTicks", 4));
        double countdownSeconds = countdownTicks * crotchet;
        double audioLead = LevelPlaybackSettings.Setting(level, "separateCountdownTime", true)
            ? countdownSeconds : 0.0;

        double[] entryTimes = new double[floors.Count];
        double[] floorBpms = new double[floors.Count];
        double chartTime = 0.0;
        for (int i = 0; i < floors.Count; i++)
        {
            double floorBpm = floors[i].bpm;
            if (i < timing.Count)
            {
                chartTime = timing[i].EntryTime;
                floorBpm = bpm * timing[i].Speed;
            }
            floorBpms[i] = double.IsFinite(floorBpm) && floorBpm > 1e-6 ? floorBpm : bpm;

            double fileTime = chartTime - audioLead + offsetSeconds;
            // Snap float angle rounding at the start of the PCM clip.
            entryTimes[i] = Math.Abs(fileTime) < 1e-8 ? 0.0 : fileTime;
        }

        // Sweep of the secondary planet around the primary. The pair always sits one tile apart, so
        // with the primary as the pole the secondary's bearing runs from "towards the previous tile"
        // to "towards the next": 180 degrees on a straight run, 180 plus the turn at a corner.
        double[] sweepStart = new double[floors.Count];
        double[] sweepDelta = new double[floors.Count];

        // Pause and Hold stretch a tile's window, but the planet's angular speed must not drop: it
        // takes extra whole turns instead of turning slower.
        for (int i = 1; i < floors.Count; i++)
        {
            // Our basis is (cos, sin), so clockwise is negative
            double direction = floors[i].isCW ? -1.0 : 1.0;
            double from = floors[i - 1].angle + 180.0;
            double to = floors[i].angle;

            if (i + 1 < floors.Count && planetTrack[i].Count == 3)
            {
                // An equilateral orbit changes the angle covered per tile. Use the same
                // interval as the timing calculator, anchored at the exit bearing.
                sweepDelta[i] = Math.Max(0, entryTimes[i + 1] - entryTimes[i]) * floorBpms[i] * 3 * direction;
                sweepStart[i] = to - sweepDelta[i];
                continue;
            }

            // Swept angle folded into [0, 360): positive along the travel direction
            double swept = Mod360((to - from) * direction);

            // Full-turn test: a U-turn collapses to 0 after the mod, but the pair
            // still circles once to swap places; only midspin tiles genuinely stand still.
            if (swept <= FullTurnEpsilonDegrees || swept >= FullTurnDegrees)
            {
                swept = floors[i].isMidspin ? 0.0 : 360.0;
            }

            // Turn the extra window from Pause / Hold into extra rotation, keeping angular speed constant
            swept += ExtraSweepDegrees(timing, i);

            sweepStart[i] = from;
            sweepDelta[i] = swept * direction;
        }

        Dictionary<int, PauseAngleCorrection>? pauseAngles = null;
        Dictionary<int, double>? pauseLocks = null;
        Dictionary<int, PauseStartTiming>? pauseStarts = null;
        foreach (var (i, (correction, ticks)) in pauseOptions)
        {
            if (i <= 0 || i >= floors.Count - 1 || i >= timing.Count || floors[i].isMidspin
                || !(timing[i].ExtraBeats > 0.0))
                continue;

            double direction = floors[i].isCW ? -1.0 : 1.0;
            double beat = 60.0 / floorBpms[i];
            double pauseBeats = timing[i].ExtraBeats;
            int leadBeats = ticks > 1 && pauseBeats >= ticks ? ticks : 4;
            (pauseStarts ??= [])[i] = new(entryTimes[i] + pauseBeats * beat, beat, leadBeats);
            double inputLock = (pauseBeats + .2) * beat;
            if (entryTimes[i + 1] - entryTimes[i] - inputLock >= .05)
                (pauseLocks ??= [])[i] = inputLock;

            double pauseSweep = timing[i].ExtraBeats * 180.0 * direction;
            // The three-planet sweep is already anchored at the exit.
            if (planetTrack[i].Count != 3)
                sweepStart[i] -= pauseSweep;

            double resetAngle = Mod360(90.0 - sweepStart[i]);
            double previousAngle = Mod360(90.0 - (sweepStart[i] + pauseSweep));
            double offset = -(int)correction * direction * Math.Abs(previousAngle - resetAngle);
            if (Math.Abs(offset) > FullTurnEpsilonDegrees)
                (pauseAngles ??= [])[i] = new PauseAngleCorrection(offset, 60.0 / floorBpms[i]);
        }

        if (floors.Count > 1)
        {
            // The opening sweep ends pointing from the start marker to floor 1. Only its visible
            // part is evaluated because the file clock starts at zero.
            double direction0 = floors[0].isCW ? -1.0 : 1.0;
            double window = Math.Max(entryTimes[1], 0.0);
            double speed = timing.Count > 0 && timing[0].Speed > 1e-6 ? timing[0].Speed : 1.0;
            sweepDelta[0] = window / crotchet * speed * 180.0 * direction0;
            sweepStart[0] = floors[0].angle - sweepDelta[0];
        }

        if (floors.Count > 1)
        {
            // The final tile keeps turning after the level ends, so give it a window of at least one
            // beat and a per-beat sweep, letting the angle keep growing when t goes past 1.
            int last = floors.Count - 1;
            double directionLast = floors[last].isCW ? -1.0 : 1.0;

            if (Math.Abs(sweepDelta[last]) < 1e-6)
            {
                sweepStart[last] = floors[last - 1].angle + 180.0;
                sweepDelta[last] = 180.0 * directionLast;
            }
        }

        return new PlaybackTimeline
        {
            EntryTimes = entryTimes,
            FloorBpms = floorBpms,
            SpeedContexts = speedContexts,
            SweepStart = sweepStart,
            SweepDelta = sweepDelta,
            PlanetTrack = planetTrack,
            PauseAngles = pauseAngles,
            PauseLocks = pauseLocks,
            PauseStarts = pauseStarts,
            LevelDuration = Math.Max(entryTimes[^1], 0.0),
            OffsetSeconds = offsetSeconds,
            SeparateCountdownLead = audioLead,
            CountdownTicks = countdownTicks,
            Pitch = pitch,
            SnapBpm = bpm,
        };
    }

    internal static double ReadPlaybackPitch(Newtonsoft.Json.Linq.JObject? settings)
    {
        double pitch = LevelPlaybackSettings.Setting(settings, "pitch", 100.0) / 100.0;
        return pitch > 1e-6 && double.IsFinite(pitch) ? pitch : 1.0;
    }

    private void SetTimelineData(PlaybackTimeline timeline)
    {
        _entryTimes = timeline.EntryTimes;
        _sweepStart = timeline.SweepStart;
        _sweepDelta = timeline.SweepDelta;
        _planetTrack = timeline.PlanetTrack;
        _pauseAngles = timeline.PauseAngles;
        _pauseStarts = timeline.PauseStarts;
        _levelDuration = timeline.LevelDuration;
        _offsetSeconds = timeline.OffsetSeconds;
        _videoCountdownLead = timeline.SeparateCountdownLead;
        _pitch = timeline.Pitch;
    }

    private void ApplyTimelineData(PlaybackTimeline timeline)
    {
        SetTimelineData(timeline);
        _currentIndex = 0;
        _startFromSelectedFloor = false;
        _playTime = 0.0;
        _isPlaying = false;
        _playbackZoomNeedsReset = true;
        _duration = _levelDuration;

        if (_floors.Count == 0)
        {
            _duration = 0.0;
            return;
        }

        // Start the camera on the first floor
        _planetCenter = new Vector2(_floors[0].position.X, _floors[0].position.Y);
        _cameraOrigin = _planetCenter;
        _cameraSnap = false;
        _cameraIndex = 0;
        _cameraFrom = _cameraTo = LevelCameraTarget(_planetCenter);
        _cameraElapsed = 0.0;
        ResetCameraHit();
        _lastCameraTime = 0.0;
        _cameraFree = false;
        _camera.Position = _cameraTo;
        _camera.MarkDirty();

        _rotationAngle = 0f;
        _isCw = _floors[0].isCW;
        ApplyTimeline();
    }

    /// <summary>
    /// Rotation a tile gains from its Pause / Hold extra window; without it the longer window would
    /// read as a slower planet.
    /// </summary>
    private static double ExtraSweepDegrees(List<FloorTimingCalculator.FloorData> timing, int index)
    {
        if (index <= 0 || index >= timing.Count)
        {
            return 0.0;
        }

        FloorTimingCalculator.FloorData data = timing[index];
        double extraBeats = data.ExtraBeats;

        if (data.HoldLength > 0)
        {
            extraBeats += data.HoldLength * 2.0;
        }

        // One beat is 180 degrees, so extra beats times 180 gives the extra rotation
        return extraBeats * 180.0;
    }

    /// <summary>Read the level start offset in milliseconds.</summary>
    private static double ReadLevelOffset(Level level)
    {
        double offsetMs = ReadSetting(level, "offset", 0.0);
        return offsetMs / 1000.0;
    }

    /// <summary>Safely read a numeric level setting.</summary>
    private static double ReadSetting(Level level, string key, double fallback)
    {
        if (!level.HasSetting(key))
        {
            return fallback;
        }

        try
        {
            return level.GetSetting<double>(key);
        }
        catch (Exception)
        {
            try
            {
                return level.GetSetting<int>(key);
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }

    /// <summary>Normalize an angle into [0, 360).</summary>
    private static double Mod360(double degrees) => (degrees % 360.0 + 360.0) % 360.0;

    /// <summary>Normalize an angle into [0, 360).</summary>
    private static float WrapAngle(double degrees)
    {
        if (!double.IsFinite(degrees))
        {
            return 0f;
        }

        double wrapped = degrees % 360.0;
        if (wrapped < 0.0)
        {
            wrapped += 360.0;
        }

        return (float)wrapped;
    }
}
