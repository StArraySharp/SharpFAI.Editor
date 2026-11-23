namespace SharpFAI.Player.Engine.Play;

public readonly record struct HitRecord(
    int Floor, HitMargin Margin, double OffsetDegrees, double Time, bool Failed = false, bool Accepted = false);

public sealed class PlaySession
{
    private const int RecentLimit = 32;
    private readonly Dictionary<int, HitMargin> _results = [];
    private readonly List<HitRecord> _recent = [];
    private ManualTrack? _track;
    private int? _holdKey;
    private int _taps;
    private double _overload;
    private double _multipress;
    private double _lastMultipressDamage;
    private int _consecutiveMultipress;
    private double _lockedUntil;
    private double _countdownBeatSeconds;
    private double _countdownGoTime;
    private Action<HitRecord>? _onHit;

    public HitStats Stats { get; } = new();
    public bool NoFail { get; set; }
    public HitRecord? LastHit => _recent.Count == 0 ? null : _recent[0];
    public IReadOnlyList<HitRecord> Recent => _recent;
    public int CurrentFloor { get; private set; }
    public int CheckpointFloor { get; private set; }
    public int TargetFloor => _track is not null && CurrentFloor < _track.LastFloor ? CurrentFloor + 1 : -1;
    public double Time { get; private set; }
    public bool Failed { get; private set; }
    public bool Completed => _track is not null && CurrentFloor >= _track.LastFloor;
    public bool Holding => _holdKey.HasValue;
    public bool ReleasedHoldEarly { get; private set; }
    internal bool FailedByHitbox { get; private set; }
    internal bool IsTracking => _track is not null;
    internal int? Countdown
    {
        get
        {
            if (_track is not { } track || _countdownBeatSeconds <= 0.0)
                return null;

            double beats = (_countdownGoTime - Time) / _countdownBeatSeconds;
            if (beats <= -1.0 || beats > track.CountdownTicks - 1 + 1e-9)
                return null;

            return Math.Max(0, (int)Math.Ceiling(beats - 1e-9));
        }
    }

    public HitMargin? GetResult(int floor) => _results.TryGetValue(floor, out HitMargin margin) ? margin : null;

    internal void Clear()
    {
        _track = null;
        _results.Clear();
        _recent.Clear();
        Stats.Clear();
        CurrentFloor = CheckpointFloor = 0;
        Time = 0.0;
        Failed = ReleasedHoldEarly = FailedByHitbox = false;
        _holdKey = null;
        _taps = 0;
        _overload = _multipress = 0.0;
        _lastMultipressDamage = double.NegativeInfinity;
        _consecutiveMultipress = 0;
        _lockedUntil = double.NegativeInfinity;
        _countdownBeatSeconds = 0.0;
    }

    internal void Start(ManualTrack track, int floor, double? seekTime = null)
    {
        Clear();
        _track = track;
        CurrentFloor = track.PlayableFloor(floor);
        CheckpointFloor = floor;
        Time = seekTime ?? Math.Min(0.0, track.Entry(CurrentFloor));
        if (CurrentFloor == 0 && track.LastFloor > 0)
            _lockedUntil = track.Entry(1) - 60.0 / track.Bpm(0);
        else
            _lockedUntil = track.Entry(CurrentFloor) + track.PauseLock(CurrentFloor);

        if (seekTime.HasValue || Completed)
            return;

        if (CurrentFloor == 0)
        {
            _countdownBeatSeconds = track.BaseBeatSeconds;
            _countdownGoTime = track.Entry(0) + (track.CountdownTicks - 1) * _countdownBeatSeconds;
        }
        else
        {
            _countdownBeatSeconds = track.CountdownBeatSecondsAt(CurrentFloor);
            _countdownGoTime = track.Entry(TargetFloor) - _countdownBeatSeconds;
            Time = _countdownGoTime - (track.CountdownTicks - 1) * _countdownBeatSeconds;
            _lockedUntil = Math.Max(_lockedUntil, _countdownGoTime);
        }
    }

    internal void UpdateTrackRules(ManualTrack track) => _track = track;

    internal HitBoundaries GetBoundaries(double multiplier) =>
        _track is { } track && TargetFloor >= 0
            ? HitJudge.GetBoundaries(track.Bpm(CurrentFloor), multiplier * track.RulesAt(TargetFloor).Margin, track.Pitch)
            : HitJudge.GetBoundaries(120.0, multiplier);

    internal void Advance(double until, double frameStart, double frameEnd,
        ReadOnlySpan<PlayInput.KeyEvent> events, double multiplier, Action<HitRecord>? onHit = null)
    {
        _onHit = onHit;
        try
        {
            AdvanceCore(until, frameStart, frameEnd, events, multiplier);
        }
        finally
        {
            _onHit = null;
        }
    }

    private void AdvanceCore(double until, double frameStart, double frameEnd,
        ReadOnlySpan<PlayInput.KeyEvent> events, double multiplier)
    {
        if (_track is not { } track)
            return;

        double start = Time;
        until = Math.Max(start, until);
        int presses = 0;
        foreach (PlayInput.KeyEvent input in events)
            presses += input.Pressed ? 1 : 0;
        if (presses == 1)
            _consecutiveMultipress = 0;

        bool landedFromPress = false;
        double frameSeconds = Math.Max(0.0, frameEnd - frameStart);
        foreach (PlayInput.KeyEvent input in events)
        {
            double fraction = frameEnd > frameStart ? Math.Clamp((input.Time - frameStart) / (frameEnd - frameStart), 0.0, 1.0) : 1.0;
            AdvanceClock(track, start + (until - start) * fraction, multiplier);
            if (Failed || Completed)
                return;
            int floor = CurrentFloor;
            HandleInput(track, input, multiplier, landedFromPress, frameSeconds);
            landedFromPress |= input.Pressed && CurrentFloor != floor;
        }

        AdvanceClock(track, until, multiplier);
    }

    private void AdvanceClock(ManualTrack track, double until, double multiplier)
    {
        until = Math.Max(Time, until);
        while (!Failed && !Completed)
        {
            int target = TargetFloor;
            double entry = track.Entry(target);
            double rate = track.AngleRate(CurrentFloor);
            ManualTrack.Rules targetRules = track.RulesAt(target);
            ManualTrack.Rules currentRules = track.RulesAt(CurrentFloor);
            bool auto = targetRules.Auto || (track.IsHold(CurrentFloor) && currentRules.Auto);
            bool startingHold = track.IsHold(CurrentFloor) && !_results.ContainsKey(CurrentFloor);
            bool safe = NoFail || currentRules.Safe;
            HitBoundaries baseBounds = HitJudge.GetBoundaries(track.Bpm(CurrentFloor), multiplier, track.Pitch);
            double deadline = auto || startingHold ? entry - .5 / rate
                : entry + HitJudge.MissThreshold(baseBounds, safe) / rate;

            if (until <= deadline)
                break;

            SetTime(track, Math.Max(Time, deadline));
            double offset = (Time - entry) * rate;
            if (auto || startingHold || safe)
                Land(track, auto ? HitMargin.Auto : startingHold ? HitMargin.Perfect : HitMargin.Miss, offset, null);
            else
                Fail(HitMargin.Miss, offset);
        }

        if (!Failed && !Completed)
            SetTime(track, until);
    }

    private void SetTime(ManualTrack track, double time)
    {
        _overload = Math.Max(0.0, _overload - .4 * (time - Time) / track.BaseBeatSeconds);
        _multipress = time - _lastMultipressDamage > 6 * track.BaseBeatSeconds ? 0.0
            : Math.Max(0.0, _multipress - .2 * (time - Time) / track.BaseBeatSeconds);
        Time = time;
    }

    private void HandleInput(ManualTrack track, PlayInput.KeyEvent input, double multiplier,
        bool landedFromPress, double frameSeconds)
    {
        int target = TargetFloor;
        if ((input.Pressed && Time < _lockedUntil) || track.RulesAt(target).Auto)
            return;

        double rate = track.AngleRate(CurrentFloor);
        double offset = (Time - track.Entry(target)) * rate;
        HitBoundaries bounds = GetBoundaries(multiplier);
        HitMargin margin = HitJudge.JudgeOffset(offset, bounds);
        bool onHold = track.IsHold(CurrentFloor);
        bool nextHold = track.IsHold(track.PlayableFloor(target));

        if (!input.Pressed)
        {
            if (_holdKey != input.Code)
                return;

            _holdKey = null;
            // Hold release uses the unscaled counted window times the destination's margin scale.
            double releaseMargin = HitJudge.GetBoundaries(track.Bpm(CurrentFloor), multiplier, track.Pitch).Counted
                * track.RulesAt(target).Margin;
            if (offset < -releaseMargin && !track.RulesAt(CurrentFloor).Safe)
            {
                if (NoFail)
                    Land(track, HitMargin.Miss, offset, null);
                else
                {
                    ReleasedHoldEarly = true;
                    Fail(HitMargin.Miss, offset);
                }
            }
            else if (!nextHold)
            {
                TryHit(track, margin, offset, null);
            }
            return;
        }

        if (onHold && !nextHold && offset >= -bounds.Counted && _holdKey.HasValue)
            return;

        // The original discards presses immediately after a hold if they are outside the next window.
        if (CurrentFloor > 0 && track.IsHold(CurrentFloor - 1) && margin == HitMargin.TooEarly
            && (Time - track.Entry(CurrentFloor)) * rate < 90.0)
            return;

        if (landedFromPress && HitJudge.IsValidHit(margin) && track.PenalizesMultipress(CurrentFloor)
            && !ApplyMultipress(track, offset, frameSeconds))
            return;

        TryHit(track, margin, offset, input.Code);
    }

    private bool ApplyMultipress(ManualTrack track, double offset, double frameSeconds)
    {
        double angle = track.TurnDegrees(CurrentFloor);
        double seconds = angle / (track.AngleRate(CurrentFloor) * track.Pitch);
        if (angle > 89.9 && seconds > Math.Max(.04, 3.5 * frameSeconds))
        {
            _multipress += .35;
            _lastMultipressDamage = Time;
        }
        else if (seconds > .02)
        {
            _consecutiveMultipress++;
        }

        if (_multipress <= 1.0 && _consecutiveMultipress <= 8)
            return true;
        if (NoFail)
            Record(new HitRecord(TargetFloor, HitMargin.Overload, offset, Time));
        else if (!track.RulesAt(CurrentFloor).Safe)
        {
            Fail(HitMargin.Overload, offset);
            return false;
        }

        _multipress = Math.Min(_multipress, .5);
        _consecutiveMultipress = 4;
        return true;
    }

    private void TryHit(ManualTrack track, HitMargin margin, double offset, int? key)
    {
        if (HitJudge.IsValidHit(margin))
        {
            if (++_taps >= track.Taps(TargetFloor))
                Land(track, margin, offset, key);
            return;
        }

        if (margin == HitMargin.TooEarly)
        {
            _overload += .5;
            if (_overload > 1.0)
            {
                if (NoFail || track.RulesAt(CurrentFloor).Safe)
                {
                    _overload = .5;
                    Record(new HitRecord(TargetFloor, HitMargin.Overload, offset, Time));
                }
                else
                {
                    Fail(HitMargin.Overload, offset);
                }
                return;
            }
        }

        Record(new HitRecord(TargetFloor, margin, offset, Time));
    }

    private void Land(ManualTrack track, HitMargin margin, double offset, int? key)
    {
        CurrentFloor++;
        Record(new HitRecord(CurrentFloor, margin, offset, Time, Accepted: true));
        while (!Completed && track.IsMidspin(CurrentFloor))
        {
            CurrentFloor++;
            Record(new HitRecord(CurrentFloor, track.RulesAt(CurrentFloor).Auto ? HitMargin.Auto : HitMargin.Perfect,
                0.0, Time, Accepted: true));
        }

        _taps = 0;
        _holdKey = track.IsHold(CurrentFloor) ? key : null;
        double pause = track.PauseLock(CurrentFloor);
        _lockedUntil = pause > 0.0 ? track.Entry(CurrentFloor) + pause : double.NegativeInfinity;
    }

    private void Fail(HitMargin margin, double offset)
    {
        Failed = true;
        Record(new HitRecord(TargetFloor, margin, offset, Time, Failed: true));
    }

    internal bool FailByHitbox()
    {
        if (!IsTracking || Failed || Completed || NoFail || _track!.RulesAt(CurrentFloor).Safe)
            return false;
        FailedByHitbox = true;
        Fail(HitMargin.Miss, 0);
        return true;
    }

    private void Record(HitRecord record)
    {
        if (record.Accepted && _track is { } track)
            CheckpointFloor = Math.Max(CheckpointFloor, track.CheckpointAt(record.Floor));

        if (record.Accepted || record.Failed)
            _results[record.Floor] = record.Margin;

        Stats.Record(record.Margin);
        _recent.Insert(0, record);
        if (_recent.Count > RecentLimit)
            _recent.RemoveAt(_recent.Count - 1);
        _onHit?.Invoke(record);
    }
}
