using SharpFAI.Player.Engine.Events;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using System.Runtime.InteropServices;

using Scheduled = SharpFAI.Player.Engine.ScheduledEvent<SharpFAI.Player.Engine.Events.TrackRangeEvent>;

namespace SharpFAI.Player.Engine;

internal sealed class RuntimeTrackTimeline
{
    private readonly record struct Tween(float From, float To, TrackTiming Timing)
    {
        public float At(double time) => From + (To - From) * Timing.Progress(time);
    }

    private struct Motion
    {
        public Tween? X, Y, ScaleX, ScaleY, Rotation, Opacity;
    }

    private readonly record struct ColorTransition(Vector4 From, float Glow, TrackTiming Timing);

    internal readonly record struct MotionBounds(Vector2 MinOffset, Vector2 MaxOffset,
        Vector2 MinScale, Vector2 MaxScale, bool Overshoots)
    {
        public MotionBounds Include(Vector2 offset, Vector2 scale, bool overshoots) =>
            new(Vector2.ComponentMin(MinOffset, offset), Vector2.ComponentMax(MaxOffset, offset),
                Vector2.ComponentMin(MinScale, scale), Vector2.ComponentMax(MaxScale, scale), Overshoots || overshoots);
    }

    private readonly FloorTrackState[] _baseline;
    private readonly TrackAnimationWindow[] _animation;
    private readonly IReadOnlyList<Floor> _floors;
    private readonly double[] _entries;
    private readonly double[]? _floorBpms;
    private readonly double _pitch;
    private readonly Scheduled[] _events;
    private readonly Dictionary<int, Motion> _motion = [];
    private readonly Dictionary<int, ColorTransition> _colors = [];
    private int _nextEvent;
    private double _time = double.NegativeInfinity;

    public RuntimeTrackTimeline(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors, FloorTrackState[] baseline,
        double[] entries, double pitch, double[]? floorBpms = null)
    {
        _baseline = baseline;
        _floors = floors;
        _entries = entries;
        _floorBpms = floorBpms;
        _pitch = pitch;
        _animation = new TrackAnimationWindow[floors.Count];
        for (int i = 0; i < floors.Count; i++)
        {
            _animation[i] = CreateAnimation(i);
        }

        _events = TimedEventSchedule.Build<TrackRangeEvent>(events, floors, entries, floorBpms);
        States = Array.Exists(_events, item => item.Event is RecolorTrack)
            ? (FloorTrackState[])baseline.Clone() : baseline;
    }

    public FloorTrackState[] States { get; }
    internal FloorTrackState[] Baseline => _baseline;
    public int MaterialRevision { get; private set; }

    private TrackAnimationWindow CreateAnimation(int floor) => TrackAnimationWindow.Create(floor, _baseline[floor],
        _floors, _entries, _pitch, _floorBpms?[floor] ?? _floors[floor].bpm);

    public FloorTimeRange VisibilityAt(int floor)
    {
        FloorTrackState state = _baseline[floor];
        if (state.Appear != TrackAnimation.Fade && state.Disappear == TrackAnimation.None)
        {
            return FloorTimeRange.All;
        }
        ref readonly TrackAnimationWindow window = ref _animation[floor];
        return new FloorTimeRange(window.Appear == TrackAnimation.Fade ? window.AppearStart : double.NegativeInfinity,
            window.Disappear == TrackAnimation.None ? double.PositiveInfinity : window.DisappearStart + window.Duration);
    }

    public Dictionary<int, MotionBounds> GetMotionBounds(CancellationToken token)
    {
        Dictionary<int, MotionBounds> result = [];
        foreach (Scheduled item in _events)
        {
            token.ThrowIfCancellationRequested();
            if (item.Event is not MoveTrack move)
            {
                continue;
            }
            EventVector? position = move.Enabled("positionOffset", move.PositionOffset);
            EventVector? scale = move.Enabled("scale", move.Scale);
            if (position is null && scale is null)
            {
                continue;
            }

            (int start, int end, long step) = ResolveRange(item);
            float spacing = Floor.length * 2f * _baseline[start].LengthRatio;
            bool overshoots = item.Timing.Ease is EventEnums.Ease.InBack or EventEnums.Ease.OutBack or EventEnums.Ease.InOutBack
                or EventEnums.Ease.InElastic or EventEnums.Ease.OutElastic or EventEnums.Ease.InOutElastic;
            Vector2 offset = new Vector2(position?.X ?? 0f, position?.Y ?? 0f) * spacing;
            for (long floor = start; floor <= end; floor += step)
            {
                int i = (int)floor;
                Vector2 baseline = _baseline[i].Transform.Scale;
                MotionBounds bounds = result.GetValueOrDefault(i, new MotionBounds(Vector2.Zero, Vector2.Zero, baseline, baseline, false));
                Vector2 size = new((scale?.X ?? baseline.X * 100f) / 100f, (scale?.Y ?? baseline.Y * 100f) / 100f);
                result[i] = bounds.Include(offset, size, overshoots);
            }
        }
        return result;
    }

    public HashSet<FloorMaterial>?[] PrepareMaterials(CancellationToken token)
    {
        Dictionary<int, FloorTrackState> states = [];
        HashSet<FloorMaterial>?[] materials = new HashSet<FloorMaterial>?[_baseline.Length];
        foreach (Scheduled item in _events)
        {
            token.ThrowIfCancellationRequested();
            if (item.Event is not RecolorTrack recolor)
            {
                continue;
            }

            (int start, int end, long step) = ResolveRange(item);
            for (long floor = start; floor <= end; floor += step)
            {
                int i = (int)floor;
                FloorTrackState current = states.GetValueOrDefault(i, _baseline[i]);
                FloorMaterial previous = current.Material;
                states[i] = current.ApplyColorFields(recolor, recolor, start);
                FloorMaterial next = states[i].Material;
                if (next != previous)
                {
                    (materials[i] ??= [_baseline[i].Material]).Add(next);
                }
            }
        }

        return materials;
    }

    public void Reset()
    {
        if (_nextEvent != 0)
        {
            foreach (int floor in _colors.Keys)
            {
                States[floor] = _baseline[floor];
            }
            _motion.Clear();
            _colors.Clear();
            MaterialRevision++;
            _nextEvent = 0;
        }

        _time = double.NegativeInfinity;
    }

    public void Advance(double time)
    {
        if (time < _time)
        {
            Reset();
        }

        while (_nextEvent < _events.Length && _events[_nextEvent].Timing.Start <= time)
        {
            Scheduled item = _events[_nextEvent++];
            Trigger(item);
        }

        _time = time;
    }

    public (FloorRenderTransform Transform, float Opacity) TransformAt(int floor, double time, bool hidePassed)
    {
        ref readonly FloorTrackState basis = ref _baseline[floor];
        FloorRenderTransform transform = basis.Transform;
        float opacity = basis.Hidden ? 0f : basis.Opacity;
        ref readonly Motion motion = ref CollectionsMarshal.GetValueRefOrNullRef(_motion, floor);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(in motion))
        {
            transform = new FloorRenderTransform(
                new Vector2(motion.X?.At(time) ?? transform.Offset.X, motion.Y?.At(time) ?? transform.Offset.Y),
                new Vector2(motion.ScaleX?.At(time) ?? transform.Scale.X, motion.ScaleY?.At(time) ?? transform.Scale.Y),
                motion.Rotation?.At(time) ?? transform.Rotation);
            if (!basis.Hidden)
                opacity = motion.Opacity?.At(time) ?? opacity;
        }
        _animation[floor].Apply(time, hidePassed, ref transform, ref opacity);
        return (transform, opacity);
    }

    public (Vector2 Offset, float Rotation) PlanetMotionAt(int floor, double time)
    {
        FloorTrackState basis = _baseline[floor];
        if (!basis.StickToFloors)
        {
            return default;
        }

        Motion motion = _motion.GetValueOrDefault(floor);
        Vector2 baseline = basis.Transform.Offset;
        return (new Vector2(motion.X?.At(time) ?? baseline.X, motion.Y?.At(time) ?? baseline.Y),
            motion.Rotation?.At(time) ?? basis.Transform.Rotation);
    }

    public Vector4 ColorAt(int floor, double time, float volume = 0f)
    {
        Vector4 target = States[floor].EvaluateColor(floor, (float)time, volume);
        return _colors.TryGetValue(floor, out ColorTransition color)
            ? Vector4.Lerp(color.From, target, color.Timing.Progress(time))
            : target;
    }

    public float GlowAt(int floor, double time)
    {
        float target = States[floor].GlowMultiplier;
        return _colors.TryGetValue(floor, out ColorTransition color)
            ? color.Glow + (target - color.Glow) * color.Timing.Progress(time)
            : target;
    }

    public FloorAppearance AppearanceAt(int floor, double time, float opacity, float volume, Vector4? colorOverride)
    {
        ref readonly FloorTrackState state = ref States[floor];
        Vector4 color = colorOverride ?? state.EvaluateColor(floor, (float)time, volume);
        float glow = state.GlowMultiplier;
        if (_colors.TryGetValue(floor, out ColorTransition transition))
        {
            float progress = transition.Timing.Progress(time);
            if (colorOverride is null)
                color = Vector4.Lerp(transition.From, color, progress);
            glow = transition.Glow + (glow - transition.Glow) * progress;
        }
        return new FloorAppearance(color, opacity, glow);
    }

    private void Move(int floor, Vector2? position, Vector2? scale, float? rotation, float? opacity,
        in TrackTiming timing)
    {
        FloorTrackState basis = _baseline[floor];
        ref Motion motion = ref CollectionsMarshal.GetValueRefOrAddDefault(_motion, floor, out _);
        if (position is Vector2 offset)
        {
            Vector2 target = basis.Transform.Offset + offset;
            Change(ref motion.X, basis.Transform.Offset.X, target.X, timing);
            Change(ref motion.Y, basis.Transform.Offset.Y, target.Y, timing);
        }

        if (scale is Vector2 size)
        {
            Change(ref motion.ScaleX, basis.Transform.Scale.X, size.X, timing);
            Change(ref motion.ScaleY, basis.Transform.Scale.Y, size.Y, timing);
        }

        if (rotation is float angle)
        {
            Change(ref motion.Rotation, basis.Transform.Rotation, angle, timing);
        }

        if (opacity is float alpha)
        {
            Change(ref motion.Opacity, basis.Opacity, alpha, timing);
        }
    }

    private static void Change(ref Tween? property, float fallback, float target, in TrackTiming timing)
    {
        if (float.IsFinite(target))
        {
            // The game completes the previous tween on this axis before starting its replacement.
            property = new Tween(property?.To ?? fallback, target, timing);
        }
    }

    private void Recolor(int floor, RecolorTrack item, int anchor, in TrackTiming timing)
    {
        FloorTrackState previous = States[floor];
        FloorTrackState next = previous.ApplyColorFields(item, item, anchor).WithDisplayColor(floor);
        if (next == previous)
        {
            return;
        }

        _colors[floor] = new ColorTransition(ColorAt(floor, timing.Start), GlowAt(floor, timing.Start), timing);
        States[floor] = next;
        if (previous.Material != next.Material)
        {
            MaterialRevision++;
        }
    }

    private void Trigger(in Scheduled item)
    {
        TrackRangeEvent track = item.Event;
        (int start, int end, long step) = ResolveRange(item);
        if (track is MoveTrack move)
        {
            float spacing = Floor.length * 2f * _baseline[start].LengthRatio;
            Vector2? position = ToVector(move.Enabled("positionOffset", move.PositionOffset)) * spacing;
            Vector2? scale = ToVector(move.Enabled("scale", move.Scale)) / 100f;
            float? rotation = move.Enabled("rotationOffset", move.RotationOffset) * (MathF.PI / 180f);
            float? opacity = move.Enabled("opacity", move.Opacity) / 100f;
            for (long floor = start; floor <= end; floor += step)
            {
                Move((int)floor, position, scale, rotation, opacity, item.Timing);
            }
        }
        else if (track is RecolorTrack recolor)
        {
            for (long floor = start; floor <= end; floor += step)
            {
                Recolor((int)floor, recolor, start, item.Timing);
            }
        }
    }

    private (int Start, int End, long Step) ResolveRange(in Scheduled item)
    {
        TrackRangeEvent track = item.Event;
        int last = States.Length - 1;
        int start = track.Enabled("startTile", track.StartTile)?.Resolve(item.Anchor, last) ?? item.Anchor;
        int end = track.Enabled("endTile", track.EndTile)?.Resolve(item.Anchor, last) ?? item.Anchor;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        long step = 1L + Math.Max(0, track.Enabled("gapLength", track.GapLength) ?? 0);
        return (start, end, step);
    }

    private static Vector2? ToVector(EventVector? value) => value is EventVector vector
        ? new Vector2(vector.X ?? float.NaN, vector.Y ?? float.NaN)
        : null;
}
