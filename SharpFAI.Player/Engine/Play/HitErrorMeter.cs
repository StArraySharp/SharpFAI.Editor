namespace SharpFAI.Player.Engine.Play;

internal readonly record struct HitErrorTick(float Offset, HitMargin Margin, double Time)
{
    internal float OpacityAt(double time)
    {
        float age = (float)Math.Clamp((time - Time) / HitErrorMeter.TickLifetime, 0, 1);
        return 1 - age * age;
    }
}

internal sealed class HitErrorMeter
{
    internal const int Capacity = 60;
    internal const double TickLifetime = 3;
    private const float Sensitivity = .2f;
    private const double HandDuration = .25;
    private readonly HitErrorTick[] _ticks = new HitErrorTick[Capacity];
    private int _next;
    private float _average, _handFrom;
    private double _handStart;
    internal int Count { get; private set; }

    internal HitErrorTick this[int index] => _ticks[(_next + Capacity - Count + index) % Capacity];

    internal void Add(double offset, HitBoundaries bounds, double now)
    {
        if (!double.IsFinite(offset) || !double.IsFinite(bounds.Counted) || bounds.Counted <= 0)
            return;

        float normalized = (float)(offset / bounds.Counted);
        if (Math.Abs(normalized) > 1)
            normalized = MathF.CopySign(1.00001f + .05f * Random.Shared.NextSingle(), normalized);
        _handFrom = HandAt(now);
        _handStart = now;
        if (Math.Abs(normalized) <= 1)
            _average += (normalized - _average) * Sensitivity;

        _ticks[_next] = new(normalized, HitJudge.JudgeOffset(offset, bounds), now);
        _next = (_next + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
    }

    internal float HandAt(double time)
    {
        float remaining = 1 - (float)Math.Clamp((time - _handStart) / HandDuration, 0, 1);
        return _average + (_handFrom - _average) * remaining * remaining * remaining;
    }

    internal void Clear()
    {
        _next = Count = 0;
        _average = _handFrom = 0;
        _handStart = 0;
    }
}
