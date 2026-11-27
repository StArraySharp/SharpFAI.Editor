using SharpFAI.Player.Engine;

namespace SharpFAI.Player;

internal readonly record struct VideoExportTiming(double Start, double Step, int FrameCount, int PrerollFrames)
{
    public static VideoExportTiming Create(PlaybackRange? range, double duration, double rate, int fps, double preroll)
    {
        var clip = range?.Limit(duration) ?? new PlaybackRange(0, duration);
        if (range is { } requested && (!requested.IsValid || !clip.IsValid))
            throw new InvalidOperationException("The selection has no time inside the chart.");

        double step = rate / fps;
        double framedDuration = clip.Duration / rate * fps;
        // Two rounded chart timestamps can turn an exact frame boundary into a tiny overshoot.
        double tolerance = range.HasValue ? Math.Max(1e-9, clip.End / step * 4e-16) : 0;
        int frames = checked((int)Math.Max(1, Math.Ceiling(framedDuration - tolerance)));
        int warmup = range.HasValue && double.IsFinite(preroll)
            ? (int)Math.Ceiling(Math.Min(clip.Start, Math.Clamp(preroll, 0, 30) * rate) / step) : 0;
        return new(clip.Start, step, frames, warmup);
    }

    public double TimeAt(int frame) => Math.Max(0, Start + frame * Step);
}
