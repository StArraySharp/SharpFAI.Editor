using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using Mode = SharpFAI.Serialization.EventEnums.CamMovementType;

namespace SharpFAI.Player.Engine;

internal sealed class CameraTimeline
{
    private readonly record struct Channel(float? From, float To, TrackTiming Timing)
    {
        public float At(double time, float fallback = 0f)
        {
            if (Timing.Duration <= 0 || time >= Timing.Start + Timing.Duration)
                return To;
            float start = From ?? fallback;
            return start + (To - start) * Timing.Progress(time);
        }

        public static Channel Fixed(float value) => new(value, value, default);
    }

    internal readonly record struct FollowStart(double Time, Vector2 Center);

    // A null start samples the level's initial follow. A non-null start restarts follow from Center.
    // Both callbacks must be seekable, in world units, and exclude camera offsets from MoveCamera and shake.
    internal delegate Vector2 FollowSampler(double time, FollowStart? start);

    private readonly record struct FollowPath(FollowSampler? Sampler, Func<double, Vector2> PlayerAt)
    {
        public Vector2 At(double time, FollowStart? start, Vector2? currentCenter = null) =>
            start is { } entry && time == entry.Time ? entry.Center
                : currentCenter ?? Sampler?.Invoke(time, start) ?? PlayerAt(time);
    }

    private readonly record struct State(Mode Mode, bool Follow, FollowStart? FollowEntry,
        int Tile, Vector2 RelativeAnchor, Channel X, Channel Y, Channel Rotation, Channel? Zoom);

    private readonly record struct Keyframe(double Time, State State);

    internal readonly record struct Sample(Vector2 Position, float Rotation, float? Zoom)
    {
        public bool CameraDisabled => Zoom == 0f;
    }

    private readonly State _initial;
    private readonly Keyframe[] _frames;
    private readonly ScheduledEvent<MoveCameraEvent>[] _schedule;
    private readonly FollowPath _follow;

    private CameraTimeline(State initial, Keyframe[] frames, ScheduledEvent<MoveCameraEvent>[] schedule,
        FollowPath follow)
    {
        _initial = initial;
        _frames = frames;
        _schedule = schedule;
        _follow = follow;
    }

    public static CameraTimeline? Build(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors,
        LevelRenderer.PlaybackTimeline timeline, LevelPlaybackSettings settings, float spacing,
        FollowSampler? followAt = null, Func<double, Vector2>? playerAt = null)
    {
        ScheduledEvent<MoveCameraEvent>[] scheduled = TimedEventSchedule.Build<MoveCameraEvent>(
                events, floors, timeline.EntryTimes, timeline.FloorBpms)
            .Where(item => double.IsFinite(item.Timing.Start) && double.IsFinite(item.Timing.Duration) &&
                item.Event.Enabled("minVfxOnly", item.Event.MinVfxOnly) != true).ToArray();
        if (scheduled.Length == 0)
            return null;

        Mode mode = settings.CameraRelativeTo;
        bool follow = mode == Mode.Player;
        Vector2 anchor = mode == Mode.Global || follow ? Vector2.Zero : Position(floors[0]);
        Vector2 start = anchor + settings.CameraOffset * spacing;
        State initial = new(mode, follow, null, 0, anchor, Channel.Fixed(start.X), Channel.Fixed(start.Y),
            Channel.Fixed(settings.CameraRotation), settings.CameraZoom == 0f ? Channel.Fixed(0f) : null);
        FollowPath followPath = new(followAt, playerAt ?? (time => Position(floors[FloorAt(timeline.EntryTimes, time)])));
        State state = initial;
        List<Keyframe> frames = new(scheduled.Length);
        foreach (ScheduledEvent<MoveCameraEvent> item in scheduled)
        {
            state = Apply(state, item, floors, settings, spacing, followPath);
            frames.Add(new Keyframe(item.Timing.Start, state));
        }
        return new CameraTimeline(initial, frames.ToArray(), scheduled, followPath);
    }

    internal CameraTimeline? Edit(IReadOnlyDictionary<int, MoveCameraEvent> replacements,
        IReadOnlyList<Floor> floors, LevelRenderer.PlaybackTimeline timeline, LevelPlaybackSettings settings, float spacing)
    {
        if (_frames.Length != _schedule.Length || replacements.Values.Any(item =>
                item.Enabled("minVfxOnly", item.MinVfxOnly) == true) ||
            !TimedEventSchedule.TryEdit(_schedule, replacements, floors, timeline.FloorBpms, out var scheduled))
            return null;
        if (scheduled.Any(item => !double.IsFinite(item.Timing.Start) || !double.IsFinite(item.Timing.Duration)))
            return null;
        int first = Array.FindIndex(_schedule, item => replacements.ContainsKey(item.Event.SourceIndex));
        if (first < 0)
            return null;
        int last = Array.FindLastIndex(_schedule, item => replacements.ContainsKey(item.Event.SourceIndex));
        var frames = (Keyframe[])_frames.Clone();
        State state = first == 0 ? _initial : frames[first - 1].State;
        for (int index = first; index < scheduled.Length; index++)
        {
            state = Apply(state, scheduled[index], floors, settings, spacing, _follow);
            frames[index] = new Keyframe(scheduled[index].Timing.Start, state);
            if (index >= last && frames[index] == _frames[index])
                break;
        }
        return new CameraTimeline(_initial, frames, scheduled, _follow);
    }

    public Sample Evaluate(double time, float baseZoom) => Evaluate(time, null, baseZoom);

    public Sample Evaluate(double time, Vector2? player, float baseZoom)
    {
        int lo = 0, hi = _frames.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_frames[mid].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }

        State state = lo > 0 ? _frames[lo - 1].State : _initial;
        Vector2 position = new(state.X.At(time), state.Y.At(time));
        if (state.Follow)
            position += _follow.At(time, state.FollowEntry, player);
        return new Sample(position, state.Rotation.At(time), state.Zoom?.At(time, baseZoom));
    }

    private static State Apply(State old, ScheduledEvent<MoveCameraEvent> item, IReadOnlyList<Floor> floors,
        LevelPlaybackSettings settings, float spacing, FollowPath followPath)
    {
        MoveCameraEvent action = item.Event;
        TrackTiming timing = item.Timing;
        Mode? authoredMode = action.Enabled("relativeTo", action.RelativeTo);
        EventVector? position = action.Enabled("position", action.Position);
        float? x = Finite(position?.X) * spacing;
        float? y = Finite(position?.Y) * spacing;
        if (authoredMode == old.Mode && position.HasValue && (!x.HasValue || !y.HasValue)
            && (old.Mode == Mode.Player || (old.Mode == Mode.Tile && old.Tile == item.Anchor)))
        {
            authoredMode = null;
        }

        Mode mode = authoredMode ?? old.Mode;
        bool legacy = settings.LegacyCameraRelativeTo && !authoredMode.HasValue;
        bool relative = mode is Mode.LastPosition or Mode.LastPositionNoRotation;
        // Complete only the interrupted axes. Calculate the implicit offset in the old parent space.
        bool setX = x.HasValue || authoredMode.HasValue;
        bool setY = y.HasValue || authoredMode.HasValue;
        Vector2 from = new(setX ? old.X.To : old.X.At(timing.Start), setY ? old.Y.To : old.Y.At(timing.Start));
        Vector2 offset = new(x ?? 0f, y ?? 0f);
        Vector2 implicitOffset = old.RelativeAnchor - from;
        Vector2 target;
        State next = old with { Mode = mode };
        if (legacy || relative)
        {
            target = position.HasValue ? from + offset : from;
        }
        else if (mode == Mode.Player)
        {
            if (!old.Follow)
            {
                Vector2 planet = followPath.PlayerAt(timing.Start);
                from -= planet;
                next = next with { Follow = true, FollowEntry = new FollowStart(timing.Start, planet) };
            }
            target = position.HasValue ? offset : implicitOffset;
        }
        else
        {
            if (old.Follow)
            {
                from += followPath.At(timing.Start, old.FollowEntry);
                next = next with { Follow = false, FollowEntry = null };
            }
            Vector2 anchor = mode == Mode.Tile ? Position(floors[item.Anchor]) : Vector2.Zero;
            next = next with { RelativeAnchor = anchor, Tile = mode == Mode.Tile ? item.Anchor : old.Tile };
            target = anchor + (position.HasValue ? offset : implicitOffset);
        }

        if (setX)
            next = next with { X = new Channel(from.X, target.X, timing) };
        if (setY)
            next = next with { Y = new Channel(from.Y, target.Y, timing) };

        float? rotation = Finite(action.Enabled("rotation", action.Rotation));
        if (rotation.HasValue || (authoredMode.HasValue && relative))
        {
            float targetRotation = rotation ?? Finite(action.Rotation) ?? 0f;
            if (!legacy && mode == Mode.LastPosition)
                targetRotation += old.Rotation.To;
            next = next with { Rotation = new Channel(old.Rotation.To, targetRotation, timing) };
        }

        if (Finite(action.Enabled("zoom", action.Zoom)) is float zoom && zoom >= 0f)
            next = next with { Zoom = new Channel(old.Zoom?.To ?? settings.CameraZoom, zoom, timing) };

        return next;
    }

    private static int FloorAt(double[] entryTimes, double time)
    {
        int lo = 0, hi = entryTimes.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (entryTimes[mid] <= time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return Math.Max(0, lo - 1);
    }

    private static float? Finite(float? value) => value is float number && float.IsFinite(number) ? number : null;

    private static Vector2 Position(Floor floor) => new(floor.position.X, floor.position.Y);
}
