using OpenTK.Mathematics;
using SharpFAI.Framework;
using Thrust.Geometry;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal static class TrackGuideMesh
{
    private static readonly VertexDeclaration Layout = new(
        (VertexAttribute.Position, VertexFormat.Float2),
        (VertexAttribute.Normal, VertexFormat.Float2),
        (VertexAttribute.TexCoord0, VertexFormat.Float2),
        (VertexAttribute.Tangent, VertexFormat.Float4),
        (VertexAttribute.Color0, VertexFormat.Float4),
        (VertexAttribute.TexCoord1, VertexFormat.Float2),
        (VertexAttribute.Custom0, VertexFormat.Float2));

    private readonly record struct Point(Vector2 Preview, Vector2 Runtime)
    {
        public Point Offset(Vector2 offset) => new(Preview + offset, Runtime + offset);
        public static Point Lerp(Point a, Point b, float t) => new(Vector2.Lerp(a.Preview, b.Preview, t),
            Vector2.Lerp(a.Runtime, b.Runtime, t));
    }

    public static Mesh? Build(IReadOnlyList<Floor> floors, FloorTrackState[] preview, FloorTrackState[] runtime,
        LevelRenderer.PlaybackTimeline timeline, float spacing, CancellationToken token)
    {
        if (!timeline.PlanetTrack.Any(floor => floor.HasHold || floor.Count == 3))
        {
            return null;
        }

        Mesh mesh = new(Layout, indexFormat: IndexFormat.UInt32) { Name = "TrackGuides" };
        try
        {
            int previousCount = 2;
            for (int i = 0; i < floors.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                PlanetTrackFloor state = timeline.PlanetTrack[i];
                Point origin = Center(i);
                float start = (float)timeline.EntryTimes[i];
                float end = i + 1 < floors.Count ? (float)timeline.EntryTimes[i + 1] : start;
                Vector2 opacity = new(preview[i].Hidden ? 0f : preview[i].Opacity,
                    runtime[i].Hidden ? 0f : runtime[i].Opacity);

                if (state.Count != previousCount)
                {
                    float bearing = (i > 0 ? floors[i - 1].angle : floors[i].angle) + 180f;
                    AddPlanetChange(mesh, origin, start, bearing, PlanetTrackFloor.ThirdAngle(floors[i].isCW),
                        spacing * state.Radius, state.Count > previousCount, opacity);
                }
                previousCount = state.Count;

                if (state.HasHold && i + 1 < floors.Count)
                {
                    AddHold(mesh, origin, Center(i + 1), floors[i].angle, timeline, i, spacing, opacity);
                    if (state.LandingAnimation)
                    {
                        Ring(mesh, Center(i + 1), spacing * .42f, spacing * .034f,
                            new Vector4(0f, start, end, 2f), new Vector4(.89f, .94f, 1f, .85f), opacity);
                    }
                }
            }

            if (mesh.IndexCount == 0)
            {
                mesh.Dispose();
                return null;
            }

            mesh.AddSubMeshForAll();
            return mesh;
        }
        catch
        {
            mesh.Dispose();
            throw;
        }

        Point Center(int floor)
        {
            var position = floors[floor].position;
            Vector2 center = new(position.X, position.Y);
            return new Point(center + preview[floor].Transform.Offset, center + runtime[floor].Transform.Offset);
        }
    }

    private static void AddHold(Mesh mesh, Point origin, Point landing, float exit,
        LevelRenderer.PlaybackTimeline timeline, int index, float spacing, Vector2 opacity)
    {
        double sweep = timeline.SweepDelta[index];
        double startAngle = timeline.SweepStart[index];
        float startRadius = timeline.PlanetTrack[index].Radius * spacing;
        float endRadius = timeline.PlanetTrack[index + 1].Radius * spacing;
        Point endPivot = landing.Offset(-PlanetTrackFloor.Direction(exit) * endRadius);
        int segments = (int)Math.Clamp(Math.Ceiling(Math.Abs(sweep) / 6.0), 24, 1536);
        float start = (float)timeline.EntryTimes[index], end = (float)timeline.EntryTimes[index + 1];
        uint first = (uint)mesh.VertexCount;
        for (int n = 0; n <= segments; n++)
        {
            float phase = (float)n / segments;
            Point point = At(phase);
            Point before = At(Math.Max(0, n - 1f) / segments);
            Point after = At(Math.Min(segments, n + 1f) / segments);
            Point normal = new(Normal(before.Preview, after.Preview), Normal(before.Runtime, after.Runtime));
            Vector4 data = new(phase, start, end, 1f);
            float width = spacing * .043f;
            Vector4 color = new(.54f, .82f, 1f, 1f);
            Vertex(mesh, new Point(point.Preview + normal.Preview * width, point.Runtime + normal.Runtime * width),
                1f, data, color, opacity);
            Vertex(mesh, new Point(point.Preview - normal.Preview * width, point.Runtime - normal.Runtime * width),
                -1f, data, color, opacity);
        }
        AppendStrip(mesh, first, segments);

        Point At(float t) => Point.Lerp(origin, endPivot, t).Offset(
            PlanetTrackFloor.Direction(startAngle + sweep * t) * (startRadius + (endRadius - startRadius) * t));
    }

    private static void AddPlanetChange(Mesh mesh, Point origin, float time, float entry, float side,
        float spacing, bool entering, Vector2 opacity)
    {
        if (!(spacing > 1e-5f))
        {
            return;
        }

        Point previous = origin.Offset(PlanetTrackFloor.Direction(entry) * spacing);
        Point extra = origin.Offset(PlanetTrackFloor.Direction(entry - side) * spacing);
        Vector4 meta = new(0f, time, time, 0f);
        Vector4 color = new(.84f, .92f, 1f, .55f);
        if (entering)
        {
            Dash(mesh, origin, previous, spacing, meta, color, opacity);
            Dash(mesh, previous, extra, spacing, meta, color, opacity);
            Dash(mesh, extra, origin, spacing, meta, color, opacity);
            Dot(mesh, extra, spacing * .24f, meta, new Vector4(.88f, .92f, 1f, .85f), opacity);
        }
        else
        {
            for (int i = 0; i < 24; i++)
            {
                Point dot = extra.Offset(PlanetTrackFloor.Direction(i * 15f) * (spacing * .23f));
                Dot(mesh, dot, spacing * .017f, meta, color, opacity);
            }
        }
    }

    private static void Dash(Mesh mesh, Point start, Point end, float spacing, Vector4 meta, Vector4 color, Vector2 opacity)
    {
        const int count = 7;
        for (int i = 0; i < count; i++)
        {
            Point a = Point.Lerp(start, end, (float)i / count);
            Point b = Point.Lerp(start, end, (i + .55f) / count);
            Stroke(mesh, a, b, spacing * .013f, meta, color, opacity);
        }
    }

    private static void Ring(Mesh mesh, Point center, float radius, float width, Vector4 data, Vector4 color, Vector2 opacity)
    {
        const int segments = 48;
        uint start = (uint)mesh.VertexCount;
        for (int i = 0; i <= segments; i++)
        {
            Vector2 unit = PlanetTrackFloor.Direction(i * 360f / segments);
            Vector2 outer = unit * (radius + width);
            Vector2 inner = unit * (radius - width);
            Vertex(mesh, center, 1f, data, color, opacity, outer);
            Vertex(mesh, center, -1f, data, color, opacity, inner);
        }
        AppendStrip(mesh, start, segments);
    }

    private static void Dot(Mesh mesh, Point center, float radius, Vector4 data, Vector4 color, Vector2 opacity)
    {
        const int segments = 16;
        uint start = (uint)mesh.VertexCount;
        Vertex(mesh, center, 0f, data, color, opacity);
        for (int i = 0; i <= segments; i++)
        {
            Vertex(mesh, center.Offset(PlanetTrackFloor.Direction(i * 360f / segments) * radius),
                1f, data, color, opacity);
        }

        for (uint i = 0; i < segments; i++)
        {
            mesh.AppendIndex(start);
            mesh.AppendIndex(start + i + 1);
            mesh.AppendIndex(start + i + 2);
        }
    }

    private static void AppendStrip(Mesh mesh, uint start, int segments)
    {
        for (uint i = 0; i < segments; i++)
        {
            QuadIndices(mesh, start + i * 2);
        }
    }

    private static void Stroke(Mesh mesh, Point a, Point b, float width, Vector4 data, Vector4 color, Vector2 opacity)
    {
        Vector2 preview = Normal(a.Preview, b.Preview) * width;
        Vector2 runtime = Normal(a.Runtime, b.Runtime) * width;
        uint start = (uint)mesh.VertexCount;
        Vertex(mesh, new Point(a.Preview + preview, a.Runtime + runtime), 1f, data, color, opacity);
        Vertex(mesh, new Point(a.Preview - preview, a.Runtime - runtime), -1f, data, color, opacity);
        Vertex(mesh, new Point(b.Preview + preview, b.Runtime + runtime), 1f, data, color, opacity);
        Vertex(mesh, new Point(b.Preview - preview, b.Runtime - runtime), -1f, data, color, opacity);
        QuadIndices(mesh, start);
    }

    private static Vector2 Normal(Vector2 a, Vector2 b)
    {
        Vector2 delta = b - a;
        return delta.LengthSquared > 1e-8f ? new Vector2(-delta.Y, delta.X).Normalized() : Vector2.UnitY;
    }

    private static void Vertex(Mesh mesh, Point point, float profile, Vector4 data, Vector4 color, Vector2 opacity,
        Vector2 cueOffset = default)
    {
        VertexWriter vertex = mesh.AppendVertex();
        vertex.SetVector2(VertexAttribute.Position, point.Preview);
        vertex.SetVector2(VertexAttribute.Normal, point.Runtime);
        vertex.SetTexCoord(0, new Vector2(profile, 0f));
        vertex.SetVector4(VertexAttribute.Tangent, data);
        vertex.SetColor(0, color);
        vertex.SetTexCoord(1, opacity);
        vertex.SetVector2(VertexAttribute.Custom0, cueOffset);
    }

    private static void QuadIndices(Mesh mesh, uint start)
    {
        mesh.AppendIndex(start);
        mesh.AppendIndex(start + 1);
        mesh.AppendIndex(start + 2);
        mesh.AppendIndex(start + 2);
        mesh.AppendIndex(start + 1);
        mesh.AppendIndex(start + 3);
    }
}
