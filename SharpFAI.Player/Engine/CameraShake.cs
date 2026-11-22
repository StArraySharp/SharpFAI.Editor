using OpenTK.Mathematics;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal sealed class CameraShake
{
    private readonly record struct Key(double End, Vector2 Offset);

    private readonly TrackTiming _timing;
    private readonly Key[] _keys;
    private readonly bool _inOut;
    private readonly bool _out;
    public double Start => _timing.Start;

    public CameraShake(TrackTiming timing, float strength, float intensity, bool fadeOut, int seed)
    {
        _timing = timing;
        string ease = timing.Ease.ToString();
        _inOut = ease.StartsWith("InOut", StringComparison.Ordinal);
        _out = ease.StartsWith("Out", StringComparison.Ordinal);
        // DOTween uses the vibrato per second and finishes at zero. With fade-out
        // enabled its steps get longer as their amplitude decreases.
        int count = (int)Math.Clamp(MathF.Round(20f * intensity) * timing.Duration, 2, 16384);
        _keys = new Key[count];
        var random = new Random(seed);
        float angle = (float)(random.NextDouble() * MathHelper.TwoPi);
        for (int i = 0; i < count - 1; i++)
        {
            if (i > 0)
                angle += (float)(random.NextDouble() * Math.PI - Math.PI * 1.5);
            float yaw = (float)((random.NextDouble() - .5) * Math.PI);
            float magnitude = fadeOut ? strength * (1f - i / (float)count) : strength;
            var (sin, cos) = MathF.SinCos(angle);
            double end = fadeOut
                ? timing.Duration * (i + 1.0) * (i + 2.0) / (count * (count + 1.0))
                : timing.Duration * (i + 1.0) / count;
            _keys[i] = new Key(end, new Vector2(cos * MathF.Cos(yaw), sin) * magnitude);
        }
        _keys[^1] = new Key(timing.Duration, Vector2.Zero);
    }

    public Vector2 At(double time)
    {
        double elapsed = time - Start;
        if (elapsed <= 0 || elapsed >= _timing.Duration)
            return Vector2.Zero;
        int first = 0;
        int last = _keys.Length - 1;
        while (first < last)
        {
            int mid = first + (last - first) / 2;
            if (_keys[mid].End < elapsed)
                first = mid + 1;
            else
                last = mid;
        }
        var previous = first == 0 ? default : _keys[first - 1];
        var next = _keys[first];
        float progress = (float)((elapsed - previous.End) / (next.End - previous.End));
        return Vector2.Lerp(previous.Offset, next.Offset,
            TrackEase.Evaluate(EventEnums.Ease.OutQuad, progress)) * Envelope(time);
    }

    private float Envelope(double time)
    {
        if (_timing.Ease == EventEnums.Ease.Linear)
            return 1f;
        float progress = _timing.Progress(time);
        return _inOut ? 1f - MathF.Abs(2f * progress - 1f) : _out ? 1f - progress : progress;
    }
}
