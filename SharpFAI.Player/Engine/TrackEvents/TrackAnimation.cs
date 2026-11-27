using OpenTK.Mathematics;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

internal enum TrackAnimation
{
    None, Assemble, Assemble_Far, Extend, Grow, Grow_Spin, Fade, Drop, Rise,
    Scatter, Scatter_Far, Retract, Shrink, Shrink_Spin,
}

/// <summary>Per-floor appear and disappear windows in song seconds.</summary>
internal readonly record struct TrackAnimationWindow(
    TrackAnimation Appear,
    TrackAnimation Disappear,
    double AppearStart,
    double AppearDuration,
    double DisappearStart,
    double Duration,
    double NextEntry,
    Vector2 Previous,
    Vector2 Next,
    Vector2 Scatter,
    float ScatterRotation)
{
    public static TrackAnimationWindow Create(
        int i, FloorTrackState state, IReadOnlyList<Floor> floors, double[] entries, double pitch, double bpm)
    {
        double beat = bpm > 1e-6 ? 60.0 / bpm : 0.5;
        double referenceBeat = state.AnimationReferenceBpm > 1e-6 ? 60.0 / state.AnimationReferenceBpm : beat;
        double duration = Math.Min(beat * 0.5, 0.5 * pitch);
        bool hasNext = i + 1 < floors.Count;
        double nextEntry = hasNext ? entries[i + 1] : double.PositiveInfinity;
        double lead = state.BeatsAhead * referenceBeat;
        bool fall = state.Appear is TrackAnimation.Drop or TrackAnimation.Rise;
        double appearDuration = fall ? Math.Max(0, lead) : duration;
        double authoredStart = entries[i] - lead * (fall ? 2 : 1);
        double chartStart = entries[0];
        // The opening group is already in place during an audio lead-in before the chart clock starts.
        double appearStart = chartStart > 0 && authoredStart <= chartStart
            ? -appearDuration : Math.Max(chartStart, authoredStart);
        var from = i == 0 ? default : floors[i - 1].position - floors[i].position;
        var to = hasNext ? floors[i + 1].position - floors[i].position : default;
        Vector2 scatter = new(Noise(i, 0), Noise(i, 1));
        return new TrackAnimationWindow(state.Appear, state.Disappear, appearStart, appearDuration,
            nextEntry + state.BeatsBehind * referenceBeat, duration, nextEntry,
            new Vector2(from.X, from.Y), new Vector2(to.X, to.Y), scatter * (Floor.length * 2f * state.LengthRatio),
            Noise(i, 2) * MathHelper.DegreesToRadians(75f));
    }

    public bool FadeReplacesOpacity(double opacityStart) =>
        Appear == TrackAnimation.Fade && AppearStart > opacityStart;

    public bool Scatters => Disappear is TrackAnimation.Scatter or TrackAnimation.Scatter_Far;

    public Vector2 ScatterOffset => Scatter * (Disappear == TrackAnimation.Scatter_Far ? 8f : 4f);

    public float ScatterProgress(double time) => MathF.Sin(Progress(time, DisappearStart, Duration) * MathF.PI * 0.5f);

    public void Apply(double time, bool hidePassed, double opacityStart, ref FloorRenderTransform transform, ref float opacity)
    {
        if (hidePassed && time >= NextEntry)
        {
            opacity = 0f;
            return;
        }

        if (FadeReplacesOpacity(opacityStart))
        {
            // FloorSetup sets the opacity to zero, then the appear effect tweens it to one.
            opacity = Progress(time, AppearStart, AppearDuration);
        }
        else if (Appear is not (TrackAnimation.None or TrackAnimation.Fade) && time < AppearStart + AppearDuration)
        {
            float elapsed = Progress(time, AppearStart, AppearDuration);
            float remaining = 1f - MathF.Sin(elapsed * MathF.PI * 0.5f);
            transform = Appear switch
            {
                TrackAnimation.Assemble => Offset(transform, Scatter * (4f * remaining), ScatterRotation * remaining),
                TrackAnimation.Assemble_Far => Offset(transform, Scatter * (8f * remaining), ScatterRotation * remaining),
                TrackAnimation.Extend => Offset(Scale(transform, 1f - remaining), Previous * remaining, 0f),
                TrackAnimation.Grow => Scale(transform, elapsed),
                TrackAnimation.Grow_Spin => Offset(Scale(transform, elapsed), Vector2.Zero, -MathF.PI * remaining),
                TrackAnimation.Drop => Fall(transform, elapsed, 1f),
                TrackAnimation.Rise => Fall(transform, elapsed, -1f),
                _ => transform,
            };
        }

        // Scatter changes motion channels and leaves opacity alone, so later MoveTrack events can still use the floor.
        if (Disappear == TrackAnimation.None || Scatters || time < DisappearStart || double.IsPositiveInfinity(DisappearStart))
        {
            return;
        }

        float progress = Progress(time, DisappearStart, Duration);
        float eased = MathF.Sin(progress * MathF.PI * 0.5f);
        transform = Disappear switch
        {
            TrackAnimation.Retract => Offset(Scale(transform, 1f - eased), Next * eased, 0f),
            TrackAnimation.Shrink => Scale(transform, 1f - progress),
            TrackAnimation.Shrink_Spin => Offset(Scale(transform, 1f - progress), Vector2.Zero, -MathF.PI * eased),
            _ => transform,
        };
        if (Disappear == TrackAnimation.Fade)
        {
            opacity *= 1f - progress;
        }
        else if (progress >= 1f)
        {
            opacity = 0f;
        }
    }

    private static float Progress(double time, double start, double duration) => time < start ? 0f
        : duration <= 0 ? 1f : (float)Math.Clamp((time - start) / duration, 0, 1);

    private static FloorRenderTransform Scale(FloorRenderTransform transform, float scale) =>
        transform with { Scale = transform.Scale * scale };

    private static FloorRenderTransform Offset(FloorRenderTransform transform, Vector2 delta, float rotation) =>
        transform with { Offset = transform.Offset + delta, Rotation = transform.Rotation + rotation };

    private static FloorRenderTransform Fall(FloorRenderTransform transform, float step, float direction) =>
        Offset(Scale(transform, Math.Min(1f, step * 8f)),
            new Vector2(0f, Floor.length * 16f * (1f - step) * direction), 0f);

    private static float Noise(int index, int axis)
    {
        uint value = unchecked((uint)index * 0x9e3779b9u + (uint)(axis + 1) * 0x85ebca6bu);
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        return (value & 0xffffu) / 32767.5f - 1f;
    }
}
