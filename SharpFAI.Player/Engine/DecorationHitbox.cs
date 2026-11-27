using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;

namespace SharpFAI.Player.Engine;

internal sealed record DecorationHitbox(bool Kills, string? Shape, Vector2 Scale, Vector2 Offset, float Rotation,
    bool AgainstDecorations, string? Planet, string? Trigger, double RepeatInterval, string[] TargetTags, string[] EventTags)
{
    public static DecorationHitbox? From(AddImageDecoration item)
    {
        if (item is AddTextDecoration || item.Hitbox is not ("Kill" or "Event" or "1" or "2"))
            return null;
        return new DecorationHitbox(item.Hitbox is "Kill" or "1", item.HitboxShape,
            new ImageDecorationTimeline.Axes(item.HitboxScale).Read(new Vector2(100f)).GetValueOrDefault(new Vector2(100f)) * .01f,
            new ImageDecorationTimeline.Axes(item.HitboxOffset).Read(Vector2.Zero).GetValueOrDefault(),
            item.HitboxRotation ?? 0f, item.HitboxDetectTarget is "Decoration" or "1",
            item.HitboxTargetPlanet, item.HitboxTriggerType, Math.Max(0, (item.HitboxRepeatInterval ?? 0f) / 1000.0),
            Split(item.HitboxDecorationTag), Split(item.HitboxEventTag));

        static string[] Split(string? tags) => tags?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    public bool CanTouchPivot => Planet is not ("Orbiting" or "2");
    public bool CanTouchOrbit => Planet is not ("Center" or "1");
    public int VertexCount => Shape is "Circle" or "Capsule" or "1" or "2" ? 24 : 4;

    public void FillVertices(Matrix4 sprite, Vector2 pixelSize, Span<Vector2> vertices)
    {
        Vector2 half = Vector2.ComponentMax(Vector2.Zero, Scale) * pixelSize * .5f;
        var (sin, cos) = MathF.SinCos(MathHelper.DegreesToRadians(Rotation));
        bool capsule = Shape is "Capsule" or "2";
        bool horizontal = Scale.X > Scale.Y;
        float capsuleRadius = Math.Min(half.X, half.Y);
        float circleRadius = Scale.Length * pixelSize.X * .5f;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector2 point;
            if (vertices.Length == 4)
                point = new Vector2(i is 0 or 3 ? -half.X : half.X, i < 2 ? -half.Y : half.Y);
            else
            {
                var (y, x) = MathF.SinCos(i * (MathF.Tau / vertices.Length));
                point = new Vector2(x, y) * (capsule ? capsuleRadius : circleRadius);
                if (capsule && horizontal)
                    point.X += MathF.CopySign(half.X - capsuleRadius, x);
                else if (capsule)
                    point.Y += MathF.CopySign(half.Y - capsuleRadius, y);
            }
            point += Offset * 100f;
            float u = (point.X * cos - point.Y * sin) / pixelSize.X;
            float v = (point.X * sin + point.Y * cos) / pixelSize.Y;
            vertices[i] = sprite.Row3.Xy + sprite.Row0.Xy * u + sprite.Row1.Xy * v;
        }
    }

    public static bool TouchesPlanet(ReadOnlySpan<Vector2> vertices, Vector2 center)
    {
        const float RadiusSquared = 30f * 30f;
        bool left = false, right = false;
        Vector2 from = vertices[^1] - center;
        foreach (var vertex in vertices)
        {
            Vector2 to = vertex - center;
            float cross = from.X * to.Y - from.Y * to.X;
            left |= cross > 0f;
            right |= cross < 0f;
            Vector2 edge = to - from;
            if (edge.LengthSquared > 0f)
            {
                float t = Math.Clamp(-Vector2.Dot(from, edge) / edge.LengthSquared, 0f, 1f);
                if ((from + edge * t).LengthSquared <= RadiusSquared)
                    return true;
            }
            from = to;
        }
        return left != right;
    }

    public static bool Overlaps(ReadOnlySpan<Vector2> first, ReadOnlySpan<Vector2> second) =>
        !Separated(first, second) && !Separated(second, first);

    internal static Box2? BroadPhaseBounds(ReadOnlySpan<Vector2> vertices)
    {
        Vector2 origin = vertices[0];
        Vector2 min = origin, max = origin, direction = Vector2.Zero;
        double area = 0;
        foreach (var point in vertices)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                return null;
            min = Vector2.ComponentMin(min, point);
            max = Vector2.ComponentMax(max, point);
            Vector2 delta = point - origin;
            area = Math.Max(area, Math.Abs((double)direction.X * delta.Y - (double)direction.Y * delta.X));
            if (direction == Vector2.Zero)
                direction = delta;
        }

        // Let the original SAT decide degenerate and nearly degenerate shapes. Leave a margin
        // for rounding in its float projections and preserve contact at a shared edge.
        Vector2 extent = max - min;
        float span = Math.Max(extent.X, extent.Y);
        Vector2 magnitude = Vector2.ComponentMax(Vector2.Abs(min), Vector2.Abs(max));
        if (area <= (double)span * span * .000001 || Math.Max(magnitude.X, magnitude.Y) >= 1e15f)
            return null;
        Vector2 margin = (magnitude + new Vector2(span)) * .000004f + new Vector2(.001f);
        return new Box2(min - margin, max + margin);
    }

    internal static bool MayOverlap(Box2? first, Box2? second) => first is not { } a || second is not { } b ||
        !(a.Max.X < b.Min.X || b.Max.X < a.Min.X || a.Max.Y < b.Min.Y || b.Max.Y < a.Min.Y);

    private static bool Separated(ReadOnlySpan<Vector2> first, ReadOnlySpan<Vector2> second)
    {
        Vector2 previous = first[^1];
        foreach (var vertex in first)
        {
            Vector2 edge = vertex - previous;
            Vector2 axis = new(-edge.Y, edge.X);
            previous = vertex;
            Project(first, axis, out float min, out float max);
            Project(second, axis, out float otherMin, out float otherMax);
            if (max < otherMin || otherMax < min)
                return true;
        }
        return false;

        static void Project(ReadOnlySpan<Vector2> points, Vector2 axis, out float min, out float max)
        {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            foreach (var point in points)
            {
                float distance = Vector2.Dot(point, axis);
                min = Math.Min(min, distance);
                max = Math.Max(max, distance);
            }
        }
    }
}
