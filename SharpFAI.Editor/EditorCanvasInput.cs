namespace SharpFAI.Editor;

internal enum PlaybackRate { Normal, Configured, Half }

internal static class EditorCanvasInput
{
    internal readonly record struct Binding(string Key, int Angle, int Fine = -1, bool Extra = false)
    {
        public int AngleFor(bool fine) => fine && Fine >= 0 ? Fine : Angle;
    }

    public static readonly Binding[] Directions =
    [
        new("Q", 135), new("W", 90), new("E", 45), new("A", 180), new("D", 0),
        new("Z", 225), new("S", 270), new("C", 315),
        new("Y", 60, 75, true), new("T", 120, 105, true),
        new("V", 240, 255, true), new("B", 300, 285, true),
        new("J", 30, 15, true), new("H", 150, 165, true),
        new("N", 210, 195, true), new("M", 330, 345, true),
    ];

    public static bool VisibleDirection(bool extra, bool horizontal, bool shift, bool backQuote) =>
        shift ? extra || horizontal && !backQuote : !extra;

    public static double SnapPosition(double value, bool free, bool diagonal)
    {
        double step = diagonal ? .707 : .5;
        return free ? value : Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    public static double Rotation(double x, double y, bool fine)
    {
        double angle = Math.Atan2(y, x) * 180 / Math.PI;
        if (!fine)
            angle = 90 - Math.Round(((450 - angle) % 360) / 15) * 15;
        return (angle % 360 + 360) % 360;
    }

    public static int WheelSpeed(int percent, float delta, bool fine, ref double remainder)
    {
        if (!float.IsFinite(delta)) return percent;
        remainder += delta;
        int steps = (int)Math.Clamp(Math.Truncate(remainder), -1000, 1000);
        remainder -= steps;
        int result = Math.Clamp(percent + steps * (fine ? 1 : 10), 1, 1000);
        if (steps != 0 && result is 1 or 1000) remainder = 0;
        return result;
    }

    public static double PlaybackMultiplier(int percent, PlaybackRate rate) => rate switch
    {
        PlaybackRate.Normal => 1,
        PlaybackRate.Half => Math.Clamp(percent, 1, 1000) / 200d,
        _ => Math.Clamp(percent, 1, 1000) / 100d
    };

    public static bool TryPlaybackPitch(double authoredPercent, double multiplier, out double combinedPercent)
    {
        combinedPercent = authoredPercent * multiplier;
        double rate = combinedPercent / 100;
        // Music is decoded at 44.1 kHz; synthesized hitsounds use 48 kHz. Both reach OpenAL as int sample rates.
        return double.IsFinite(rate) && rate * 44100 >= 1 && rate * 48000 <= int.MaxValue;
    }
}
