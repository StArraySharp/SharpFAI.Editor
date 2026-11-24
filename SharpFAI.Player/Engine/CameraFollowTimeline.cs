using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

/// <summary>Samples automatic camera follow on the playback clock, including across seeks.</summary>
internal sealed class CameraFollowTimeline
{
    private readonly IReadOnlyList<Floor> _floors;
    private readonly LevelRenderer.PlaybackTimeline _timeline;
    private readonly LevelPlaybackSettings _settings;
    private readonly float _spacing;
    private readonly Func<int, double, Vector2> _anchorAt;
    private readonly Vector2[] _targets;
    private readonly Vector2[] _landingCenters;
    private readonly Vector2[] _outgoingHoldOffsets;
    private readonly Dictionary<CameraTimeline.FollowStart, Vector2> _restartTargets = [];
    private int _prepared;

    // anchorAt must be safe to call out of playback order and returns the planet anchor in world units.
    public CameraFollowTimeline(IReadOnlyList<Floor> floors, LevelRenderer.PlaybackTimeline timeline,
        LevelPlaybackSettings settings, float spacing, Func<int, double, Vector2>? anchorAt = null)
    {
        _floors = floors;
        _timeline = timeline;
        _settings = settings;
        _spacing = spacing;
        _anchorAt = anchorAt ?? StaticAnchor;
        _targets = new Vector2[floors.Count];
        _landingCenters = new Vector2[floors.Count];
        _outgoingHoldOffsets = new Vector2[floors.Count];
        PrepareThrough(floors.Count - 1);
    }

    internal void InvalidateFrom(double time)
    {
        double[] entries = _timeline.EntryTimes;
        int first = LevelRenderer.FloorAtTime(entries, time);
        while (first > 0 && entries[first - 1] >= time)
            first--;
        _prepared = Math.Min(_prepared, first);
        _restartTargets.Clear();
    }

    private void PrepareThrough(int end)
    {
        for (int floor = _prepared; floor <= end; floor++)
        {
            double landing = _timeline.EntryTimes[floor];
            _targets[floor] = _anchorAt(floor, landing);
            if (floor == 0)
            {
                _landingCenters[floor] = _targets[floor];
                continue;
            }

            // Advance the outgoing segment to its limit before resetting the landing timer.
            int previous = floor - 1;
            _outgoingHoldOffsets[previous] = HoldOffsetAt(previous, landing);
            _landingCenters[floor] = CameraMotion.Follow(_landingCenters[previous], _targets[previous],
                _outgoingHoldOffsets[previous], landing - _timeline.EntryTimes[previous], _timeline.FloorBpms[previous]);
        }
        _prepared = Math.Max(_prepared, end + 1);
    }

    public Vector2 Follow(double time, CameraTimeline.FollowStart? start)
    {
        if (_targets.Length == 0)
            return start?.Center ?? Vector2.Zero;
        if (start is { } entry && time <= entry.Time)
            return entry.Center;

        double[] entries = _timeline.EntryTimes;
        int floor = LevelRenderer.FloorAtTime(entries, time);
        PrepareThrough(floor);
        Vector2 from = _landingCenters[floor];
        Vector2 target = _targets[floor];
        double segmentStart = entries[floor];
        if (start is { } restart)
        {
            int previous = LevelRenderer.FloorAtTime(entries, restart.Time);
            from = restart.Center;
            if (!_restartTargets.TryGetValue(restart, out target))
            {
                target = _anchorAt(previous, restart.Time);
                _restartTargets.Add(restart, target);
            }
            segmentStart = restart.Time;
            for (; previous < floor; previous++)
            {
                double landing = entries[previous + 1];
                from = CameraMotion.Follow(from, target, _outgoingHoldOffsets[previous], landing - segmentStart,
                    _timeline.FloorBpms[previous]);
                if (from == _landingCenters[previous + 1])
                {
                    from = _landingCenters[floor];
                    target = _targets[floor];
                    segmentStart = entries[floor];
                    break;
                }
                target = _targets[previous + 1];
                segmentStart = landing;
            }
        }

        return CameraMotion.Follow(from, target, HoldOffsetAt(floor, time), time - segmentStart,
            _timeline.FloorBpms[floor]);
    }

    public Vector2 PlayerAt(double time)
    {
        if (_targets.Length == 0)
            return Vector2.Zero;
        int floor = LevelRenderer.FloorAtTime(_timeline.EntryTimes, time);
        Vector2 anchor = _anchorAt(floor, time);
        return anchor + HoldOffsetAt(floor, time, anchor);
    }

    private Vector2 HoldOffsetAt(int floor, double time, Vector2? floorAnchor = null)
    {
        if (floor + 1 >= _floors.Count || !_timeline.PlanetTrack[floor].HasHold)
            return Vector2.Zero;

        double[] entries = _timeline.EntryTimes;
        double begin = floor == 0 ? 0.0 : entries[floor];
        double span = entries[floor + 1] - begin;
        if (span <= 1e-9 || time <= begin)
            return Vector2.Zero;

        float progress = (float)Math.Clamp(EaseProgress(Math.Min((time - begin) / span, 1.0)), 0, 1);
        Vector2 anchor = floorAnchor ?? _anchorAt(floor, time);
        Vector2 landing = _anchorAt(floor + 1, time);
        float radius = _timeline.PlanetTrack[floor + 1].Radius * _spacing;
        return PlanetTrackFloor.HoldPivot(anchor, landing, _floors[floor].angle, radius, progress) - anchor;
    }

    private double EaseProgress(double progress)
    {
        int parts = _settings.PlanetEaseParts;
        EventEnums.Ease ease = _settings.PlanetEase;
        if (progress <= 0 || progress >= 1 || parts <= 0 || ease == EventEnums.Ease.Linear)
            return progress;

        double scaled = progress * parts;
        int part = (int)scaled;
        float fraction = (float)(scaled - part);
        float eased = _settings.MirrorPlanetEase && part % 2 == 1
            ? 1f - TrackEase.Evaluate(ease, 1f - fraction)
            : TrackEase.Evaluate(ease, fraction);
        return (part + eased) / parts;
    }

    private Vector2 StaticAnchor(int floor, double time)
    {
        var position = _floors[floor].position;
        return new Vector2(position.X, position.Y);
    }
}
