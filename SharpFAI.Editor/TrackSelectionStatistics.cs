namespace SharpFAI.Editor;

internal readonly record struct TrackSelectionStatistics(double Duration, double Beats)
{
    public double Angle => Beats * 180;

    public static TrackSelectionStatistics? Calculate(ReadOnlySpan<double> entryTimes, ReadOnlySpan<double> bpms,
        int first, int last, bool excludeLast)
    {
        if (first < 0 || first > last || last >= entryTimes.Length || first >= bpms.Length)
            return null;

        // A tile's span ends at the next entry time. The final tile has no outgoing span.
        int end = excludeLast && last > first ? last : Math.Min(last + 1, entryTimes.Length - 1);
        double duration = Math.Max(0, entryTimes[end] - entryTimes[first]);
        double beats = duration * bpms[first] / 60;
        // Express the elapsed time in beats at the first selected tile's BPM, as EditorTweaks does.
        return double.IsFinite(beats) ? new(duration, beats) : null;
    }
}
