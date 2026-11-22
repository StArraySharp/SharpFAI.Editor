using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal readonly record struct TrackTiming(double Start, double Duration, EventEnums.Ease Ease)
{
    public float Progress(double time) => Duration <= 0
        ? 1f
        : TrackEase.Evaluate(Ease, (float)Math.Clamp((time - Start) / Duration, 0.0, 1.0));
}
