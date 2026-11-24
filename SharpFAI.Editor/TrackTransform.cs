namespace SharpFAI.Editor;

internal readonly record struct TrackTransform(double Degrees, bool Reflect = false)
{
    public static TrackTransform HorizontalFlip => new(180, true);
    public static TrackTransform VerticalFlip => new(0, true);

    public double Angle(double angle) => Degrees + (Reflect ? -angle : angle);

    public (double? X, double? Y) Position(double? x, double? y)
    {
        if (Reflect)
            y = -y;
        return Degrees switch
        {
            0 or 360 or -360 => (x, y),
            90 or -270 => (-y, x),
            180 or -180 => (-x, -y),
            270 or -90 => (y, -x),
            _ when x is null && y is null => (null, null),
            _ => Rotate(x ?? 0, y ?? 0),
        };
    }

    private (double X, double Y) Rotate(double x, double y)
    {
        var (sin, cos) = Math.SinCos(Degrees * Math.PI / 180);
        return (x * cos - y * sin, x * sin + y * cos);
    }
}
