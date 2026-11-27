using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;

namespace SharpFAI.Player.Engine.Play;

internal sealed class ManualTrack
{
    internal readonly record struct Rules(double Margin, bool Auto, bool Safe);
    private readonly record struct Change(int Floor, Rules Rules);

    private readonly IReadOnlyList<Floor> _floors;
    private readonly LevelRenderer.PlaybackTimeline _timeline;
    private readonly Change[] _changes;
    private readonly Dictionary<int, int> _taps = [];
    private readonly Dictionary<int, int> _checkpoints = [];

    public ManualTrack(IReadOnlyList<Floor> floors, LevelRenderer.PlaybackTimeline timeline,
        IReadOnlyList<PlaybackEvent> events)
    {
        _floors = floors;
        _timeline = timeline;
        List<Change> changes = [];
        Rules rules = new(1.0, false, false);
        foreach (PlaybackEvent item in events
            .Where(item => item.IsActive && (uint)item.Floor < (uint)floors.Count
                && item is ScaleMarginEvent or AutoPlayTilesEvent or MultitapEvent or CheckpointEvent)
            .OrderBy(item => item.Floor))
        {
            Rules previous = rules;
            switch (item)
            {
                case ScaleMarginEvent scale when scale.Enabled("scale", scale.Scale) is double value && double.IsFinite(value):
                    rules = rules with { Margin = Math.Max(0.0, value / 100.0) };
                    break;
                case AutoPlayTilesEvent auto:
                    rules = rules with { Auto = auto.Enabled("enabled", auto.Auto) ?? rules.Auto,
                        Safe = auto.Enabled("safetyTiles", auto.Safe) ?? rules.Safe };
                    break;
                case MultitapEvent multitap when multitap.Enabled("taps", multitap.Taps) is int count:
                    _taps[item.Floor] = Math.Max(1, count);
                    break;
                case CheckpointEvent checkpoint:
                    int offset = checkpoint.Enabled("tileOffset", checkpoint.TileOffset) ?? 0;
                    _checkpoints[item.Floor] = (int)Math.Clamp((long)item.Floor + offset, 0, Math.Max(0, LastFloor - 1));
                    break;
            }
            if (previous != rules)
                changes.Add(new Change(item.Floor, rules));
        }
        _changes = changes.ToArray();
    }

    public int LastFloor => _floors.Count - 1;
    public int CountdownTicks => _timeline.CountdownTicks;
    public double BaseBeatSeconds => 60.0 / _timeline.SnapBpm;
    public double CountdownBeatSecondsAt(int floor) => 120.0 / (Bpm(floor) * _timeline.PlanetTrack[floor].Count);
    public double Pitch => _timeline.Pitch;
    public double Entry(int floor) => _timeline.EntryTimes[floor];
    public double Bpm(int floor) => _timeline.FloorBpms[floor];

    internal (double Authored, double? Effective)? TempoAt(int floor) => _timeline.TempoAt(floor);

    public double AngleRate(int floor) => Bpm(floor) * 3.0;
    public bool IsMidspin(int floor) => _floors[floor].isMidspin;
    public bool IsHold(int floor) => _timeline.PlanetTrack[floor].HasHold;
    public double PauseLock(int floor) => _timeline.PauseLocks?.GetValueOrDefault(floor) ?? 0.0;
    internal bool TryGetPauseStart(int floor, out PauseStartTiming timing)
    {
        timing = default;
        return _timeline.PauseStarts?.TryGetValue(floor, out timing) == true;
    }
    internal double MinimumPauseStartTime => _timeline.OffsetSeconds;
    public int Taps(int floor) => _taps.GetValueOrDefault(floor, 1);
    public int CheckpointAt(int floor) => _checkpoints.GetValueOrDefault(floor, -1);

    public int PlayableFloor(int floor)
    {
        while (floor < LastFloor && IsMidspin(floor))
            floor++;
        return floor;
    }

    public double TurnDegrees(int floor)
    {
        Floor tile = _floors[floor];
        double turn = (tile.exitAngle - tile.entryAngle) * (tile.isCW ? 1.0 : -1.0);
        return (turn % 360.0 + 360.0) % 360.0;
    }

    public bool PenalizesMultipress(int floor)
    {
        int second = PlayableFloor(floor + 1);
        int third = PlayableFloor(second + 1);
        int fourth = PlayableFloor(third + 1);
        if (fourth > LastFloor)
            return false;

        double a = TurnDegrees(floor), b = TurnDegrees(second), c = TurnDegrees(third), d = TurnDegrees(fourth);
        const double wide = 30.1, narrow = 15.1;
        return !(a < wide && (b > wide || (b < wide && c > wide))
            || a < narrow && b < narrow && c < narrow && d > narrow);
    }

    public Rules RulesAt(int floor)
    {
        int lo = 0, hi = _changes.Length;
        while (lo < hi)
        {
            int middle = lo + ((hi - lo) >> 1);
            if (_changes[middle].Floor <= floor)
                lo = middle + 1;
            else
                hi = middle;
        }
        return lo == 0 ? new Rules(1.0, false, false) : _changes[lo - 1].Rules;
    }
}
