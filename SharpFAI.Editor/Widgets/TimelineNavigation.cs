namespace SharpFAI.Editor.Widgets;

internal static class TimelineNavigation
{
    public static double ClampStart(double start, double length, double duration) =>
        Math.Clamp(start, 0, Math.Max(0, duration - length));

    public static double DragScroll(double start, double length, double duration,
        double pointer, double margin, double seconds)
    {
        double pressure = pointer < margin
            ? -(margin - pointer) / margin
            : Math.Max(0, (pointer - (1 - margin)) / margin);
        pressure = Math.Clamp(pressure, -1, 1);
        double distance = pressure * Math.Abs(pressure) * length * seconds;
        return ClampStart(start + distance, length, duration);
    }

    public static double KeepPlayheadInView(double start, double length, double duration,
        double position, double previous, double margin, bool playing)
    {
        if (position == previous)
            return start;
        if (playing && position > previous)
            start = position < start ? position - length * margin : Math.Max(start, position - length * (1 - margin));
        else if (playing && position < previous)
            start = position > start + length ? position - length * (1 - margin) : Math.Min(start, position - length * margin);
        else if (position < start || position > start + length)
            start = position - length * .5;
        return ClampStart(start, length, duration);
    }

    public static double SeekTime(double pointerTime, double duration, bool snap,
        EditorBeatClock clock, EditorTimeSignatures signatures)
    {
        double time = Math.Clamp(pointerTime, 0, duration);
        if (snap && signatures.SnapWithin(clock.BeatAt(time), clock.BeatAt(0), clock.BeatAt(duration)) is double beat)
            time = Math.Clamp(clock.TimeAt(beat), 0, duration);
        return time;
    }
}
