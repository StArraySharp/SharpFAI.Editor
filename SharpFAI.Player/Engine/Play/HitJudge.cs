namespace SharpFAI.Player.Engine.Play;

public enum HitMargin
{
    TooEarly,
    VeryEarly,
    EarlyPerfect,
    Perfect,
    LatePerfect,
    VeryLate,
    TooLate,
    Miss,
    Overload,
    Auto,
}

public sealed class HitStats
{
    private readonly int[] _counts = new int[Enum.GetValues<HitMargin>().Length];

    public int this[HitMargin margin] => _counts[(int)margin];

    public int Total { get; private set; }

    public void Record(HitMargin margin)
    {
        _counts[(int)margin]++;
        Total++;
    }

    public void Clear()
    {
        Array.Clear(_counts);
        Total = 0;
    }

    public double Accuracy
    {
        get
        {
            int pure = this[HitMargin.Perfect] + this[HitMargin.Auto];
            int scored = Total + this[HitMargin.Miss] + this[HitMargin.Overload];
            return scored == 0 ? 1.0
                : (double)(pure + this[HitMargin.EarlyPerfect] + this[HitMargin.LatePerfect]) / scored + pure * .0001;
        }
    }
}

public readonly record struct HitBoundaries(double Counted, double Perfect, double Pure);

public static class HitJudge
{
    public static HitBoundaries GetBoundaries(double bpmTimesSpeed, double marginMultiplier = 1.0, double pitch = 1.0)
    {
        double degreesPerSecond = bpmTimesSpeed * pitch * 3.0;
        return new HitBoundaries(
            Math.Max(60.0 * marginMultiplier, .065 * degreesPerSecond),
            Math.Max(45.0 * marginMultiplier, .030 * degreesPerSecond),
            Math.Max(30.0 * marginMultiplier, .025 * degreesPerSecond));
    }

    /// <summary>Judges clockwise-positive game angles.</summary>
    public static HitMargin Judge(double hitAngleDegrees, double referenceAngleDegrees, bool isCw,
        double bpmTimesSpeed, double marginMultiplier = 1.0, double pitch = 1.0) =>
        JudgeOffset((hitAngleDegrees - referenceAngleDegrees) * (isCw ? 1.0 : -1.0),
            GetBoundaries(bpmTimesSpeed, marginMultiplier, pitch));

    internal static HitMargin JudgeOffset(double offset, HitBoundaries bounds)
    {
        if (offset > bounds.Counted) return HitMargin.TooLate;
        if (offset > bounds.Perfect) return HitMargin.VeryLate;
        if (offset > bounds.Pure) return HitMargin.LatePerfect;
        if (offset > -bounds.Pure) return HitMargin.Perfect;
        if (offset > -bounds.Perfect) return HitMargin.EarlyPerfect;
        return offset > -bounds.Counted ? HitMargin.VeryEarly : HitMargin.TooEarly;
    }

    public static bool IsValidHit(HitMargin margin) => margin is >= HitMargin.VeryEarly and <= HitMargin.VeryLate;

    public static bool IsPerfect(HitMargin margin) => margin is >= HitMargin.EarlyPerfect and <= HitMargin.LatePerfect;

    internal static double MissThreshold(HitBoundaries bounds, bool safe) =>
        safe ? bounds.Counted * 1.01 : Math.Max(180.0, bounds.Counted * 2.0);

    public static string Label(HitMargin margin) => margin switch
    {
        HitMargin.TooEarly => "太早",
        HitMargin.VeryEarly => "很早",
        HitMargin.EarlyPerfect => "早 Perfect",
        HitMargin.Perfect => "Perfect",
        HitMargin.LatePerfect => "晚 Perfect",
        HitMargin.VeryLate => "很晚",
        HitMargin.TooLate => "太晚",
        HitMargin.Overload => "过载",
        HitMargin.Auto => "自动",
        _ => "漏击",
    };
}
