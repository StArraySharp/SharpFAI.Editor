using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using Mode = SharpFAI.Serialization.EventEnums.CamMovementType;

namespace SharpFAI.Player.Engine;

internal sealed class CameraTimeline
{
    private readonly record struct Channel(float? From, float To, TrackTiming Timing)
    {
        public float At(double time, float fallback = 0f) => Interpolate(Timing.Progress(time), fallback);

        public float Interpolate(float progress, float fallback = 0f)
        {
            float start = From ?? fallback;
            return start + (To - start) * progress;
        }

        public static Channel Fixed(float value) => new(value, value, default);
    }

    private readonly record struct PositionChannel(Channel Tween, bool StartFollowsPlayer)
    {
        public float At(double time, float player, bool follow)
        {
            float progress = Tween.Timing.Progress(time);
            // Add the smoothed player center to whichever endpoints are in player space.
            float weight = StartFollowsPlayer == follow ? (follow ? 1f : 0f)
                : (follow ? progress : 1f - progress);
            return Tween.Interpolate(progress) + player * weight;
        }
    }

    private readonly record struct State(Mode Mode, bool Follow, int Tile, Vector2 RelativeAnchor,
        PositionChannel X, PositionChannel Y, Channel Rotation, Channel? Zoom);

    private readonly record struct Keyframe(double Time, State State);

    internal readonly record struct Sample(Vector2 Position, float Rotation, float? Zoom);

    private readonly State _initial;
    private readonly Keyframe[] _frames;
    private readonly ScheduledEvent<MoveCameraEvent>[] _schedule;

    private CameraTimeline(State initial, Keyframe[] frames, ScheduledEvent<MoveCameraEvent>[] schedule)
    {
        _initial = initial;
        _frames = frames;
        _schedule = schedule;
    }

    public static CameraTimeline? Build(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors,
        LevelRenderer.PlaybackTimeline timeline, LevelPlaybackSettings settings, float spacing)
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
        State initial = new(mode, follow, 0, anchor, new(Channel.Fixed(start.X), follow),
            new(Channel.Fixed(start.Y), follow), Channel.Fixed(settings.CameraRotation), null);
        State state = initial;
        List<Keyframe> frames = new(scheduled.Length);
        foreach (ScheduledEvent<MoveCameraEvent> item in scheduled)
        {
            state = Apply(state, item, floors, settings, spacing);
            frames.Add(new Keyframe(item.Timing.Start, state));
        }
        return frames.Count > 0 ? new CameraTimeline(initial, frames.ToArray(), scheduled) : null;
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
            state = Apply(state, scheduled[index], floors, settings, spacing);
            frames[index] = new Keyframe(scheduled[index].Timing.Start, state);
            if (index >= last && frames[index] == _frames[index])
                break;
        }
        return new CameraTimeline(_initial, frames, scheduled);
    }

    public Sample Evaluate(double time, Vector2 player, float baseZoom)
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
        Vector2 position = new(state.X.At(time, player.X, state.Follow), state.Y.At(time, player.Y, state.Follow));
        return new Sample(position, state.Rotation.At(time), state.Zoom?.At(time, baseZoom));
    }

    private static State Apply(State old, ScheduledEvent<MoveCameraEvent> item, IReadOnlyList<Floor> floors,
        LevelPlaybackSettings settings, float spacing)
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
        State next = old with { Mode = mode };
        if (position.HasValue || authoredMode.HasValue)
        {
            // Unity completes the interrupted channels before reading their values as tween starts.
            bool setX = x.HasValue || authoredMode.HasValue;
            bool setY = y.HasValue || authoredMode.HasValue;
            Vector2 from = new(old.X.Tween.To, old.Y.Tween.To);
            Vector2 target = new(x ?? 0f, y ?? 0f);
            bool follow = old.Follow;
            Vector2 anchor = old.RelativeAnchor;
            int tile = old.Tile;
            if (legacy || relative)
            {
                target += from;
            }
            else
            {
                follow = mode == Mode.Player;
                anchor = mode == Mode.Tile ? Position(floors[item.Anchor]) : Vector2.Zero;
                if (mode == Mode.Tile)
                    tile = item.Anchor;
                if (!position.HasValue)
                    target = old.RelativeAnchor - from;
                target += anchor;
            }

            next = next with { Follow = follow, Tile = tile, RelativeAnchor = anchor };
            if (setX)
                next = next with { X = new PositionChannel(new Channel(from.X, target.X, timing), old.Follow) };
            if (setY)
                next = next with { Y = new PositionChannel(new Channel(from.Y, target.Y, timing), old.Follow) };
        }

        float? rotation = Finite(action.Enabled("rotation", action.Rotation));
        if (rotation.HasValue || (authoredMode.HasValue && relative))
        {
            float target = rotation ?? Finite(action.Rotation) ?? 0f;
            if (!legacy && mode == Mode.LastPosition && (position.HasValue || authoredMode.HasValue))
                target += old.Rotation.To;
            next = next with { Rotation = new Channel(old.Rotation.To, target, timing) };
        }

        if (Finite(action.Enabled("zoom", action.Zoom)) is float zoom && zoom > 0f)
            next = next with { Zoom = new Channel(old.Zoom?.To ?? settings.CameraZoom, zoom, timing) };

        return next;
    }

    private static float? Finite(float? value) => value is float number && float.IsFinite(number) ? number : null;

    private static Vector2 Position(Floor floor) => new(floor.position.X, floor.position.Y);
}
