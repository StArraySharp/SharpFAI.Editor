using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

internal sealed class PlanetDecorationTrail
{
    private readonly PlanetTrail.History _history = new(128);
    private double _time = double.NaN;
    private Vector2 _position;
    private Vector2 _coordinateScale;
    private int _timingVersion;
    private string? _relativeTo;

    internal int Frame { get; private set; }

    internal ReadOnlySpan<PlanetTrail.Sample> Sample(ImageDecorationTimeline.Item item, ImageDecorationTimeline.Pose pose,
        double time, double lifetime, int frame, Vector2 position, Vector2 coordinateScale, ref PlanetTrail.Sample[] samples)
    {
        Frame = frame;
        if (!double.IsFinite(time) || !float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            _history.Clear();
            _time = double.NaN;
            return [];
        }

        bool reset = double.IsNaN(_time) || time < _time || time - _time > lifetime ||
            _timingVersion != item.TimingVersion || _relativeTo != pose.RelativeTo || _coordinateScale != coordinateScale ||
            item.HasTrailCut(_time, time) || time == _time && Vector2.DistanceSquared(_position, position) >= .25f;
        if (reset)
            _history.Clear();
        if (reset || time > _time)
        {
            _history.Record(position, new Vector4(pose.Tint.Xyz, 1f), time, lifetime, preserveHistory: true);
            _time = time;
            _position = position;
            _coordinateScale = coordinateScale;
            _timingVersion = item.TimingVersion;
            _relativeTo = pose.RelativeTo;
        }

        Vector3? colorOverride = item.UsesDefaultPlanetTrailRamp && pose.Tint.Xyz == item.Initial.Tint.Xyz
            ? null : pose.Tint.Xyz;
        return _history.Build(time, lifetime, colorOverride, ref samples);
    }
}
