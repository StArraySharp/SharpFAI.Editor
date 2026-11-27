namespace SharpFAI.Player.Engine;

/// <summary>A half-open interval measured in chart seconds.</summary>
internal readonly record struct PlaybackRange(double Start, double End)
{
    public double Duration => End - Start;
    public bool IsValid => double.IsFinite(Start) && double.IsFinite(End) && Start >= 0 && End > Start;

    public PlaybackRange Limit(double duration) => new(Math.Min(Start, duration), Math.Min(End, duration));
}
