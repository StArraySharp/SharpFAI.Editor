namespace SharpFAI.Editor.Widgets;

internal static class TimelineFloorTimes
{
    public static int LowerBound(ReadOnlySpan<double> entries, double time, int first = 0)
    {
        int last = entries.Length;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (entries[middle] < time)
                first = middle + 1;
            else
                last = middle;
        }
        return first;
    }

    public static int Nearest(ReadOnlySpan<double> entries, double time, double tolerance, int selected)
    {
        if (entries.IsEmpty)
            return -1;
        int next = LowerBound(entries, time);
        int nearest = next == entries.Length || next > 0 && time - entries[next - 1] < entries[next] - time
            ? next - 1 : next;
        double timestamp = entries[nearest];
        if (Math.Abs(timestamp - time) > tolerance)
            return -1;
        // Keep the selected brick when a midspin shares a timestamp with another brick.
        return (uint)selected < (uint)entries.Length && entries[selected] == timestamp
            ? selected : LowerBound(entries, timestamp);
    }
}
