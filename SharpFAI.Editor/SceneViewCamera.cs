using OpenTK.Mathematics;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed class SceneViewCamera
{
    private const float HalfFovTangent = .41421356f;
    private float _yaw = -.45f;
    private float _elevation = .86f;
    private float _distanceInTiles = 8.5f;

    public Vector3d Pivot { get; private set; }
    public bool Orthographic { get; set; }

    public void Focus(Vector3d point, bool resetDistance)
    {
        Pivot = point;
        if (resetDistance) _distanceInTiles = 8.5f;
    }

    public void Orbit(Vector2 pixels)
    {
        _yaw = MathF.IEEERemainder(_yaw - pixels.X * .009f, MathF.Tau);
        _elevation = Math.Clamp(_elevation + pixels.Y * .009f, -MathF.PI / 2, MathF.PI / 2);
    }

    public void Pan(Vector2 delta, float height, float tile)
    {
        var view = CreateProjection(new Vector2(1, height), tile);
        Pivot += (Vector3d)(view.Up * delta.Y - view.Right * delta.X) / view.Scale;
    }

    public void Zoom(float wheel) =>
        _distanceInTiles = Math.Clamp(_distanceInTiles * MathF.Pow(.82f, wheel), .45f, 2_500f);

    public void SnapAxis(int axis)
    {
        var view = CreateProjection(Vector2.One, 1);
        bool reverse = view.Normal[axis] > .999f;
        (_yaw, _elevation) = axis switch
        {
            0 => (reverse ? -MathF.PI / 2 : MathF.PI / 2, 0f),
            1 => (reverse ? 0f : MathF.PI, 0f),
            _ => (0f, reverse ? -MathF.PI / 2 : MathF.PI / 2),
        };
        Orthographic = true;
    }

    public Projection CreateProjection(Vector2 size, float tile)
    {
        (float sy, float cy) = MathF.SinCos(_yaw);
        (float se, float ce) = MathF.SinCos(_elevation);
        Vector3 right = new(cy, sy, 0);
        Vector3 normal = new(sy * ce, -cy * ce, se);
        float distance = _distanceInTiles * tile;
        return new(right, Vector3.Cross(normal, right), normal, size * .5f,
            distance, size.Y * .5f / (distance * HalfFovTangent), Orthographic);
    }

    public SceneRenderView CreateRenderView(Vector2 size, float tile)
    {
        var view = CreateProjection(size, tile);
        Vector2 origin = new((float)Pivot.X, (float)Pivot.Y);
        Vector3 remainder = new((float)(Pivot.X - origin.X), (float)(Pivot.Y - origin.Y), (float)Pivot.Z);
        var camera = new Matrix4(
            new Vector4(view.Right.X, view.Up.X, view.Normal.X, 0),
            new Vector4(view.Right.Y, view.Up.Y, view.Normal.Y, 0),
            new Vector4(view.Right.Z, view.Up.Z, view.Normal.Z, 0),
            new Vector4(-Vector3.Dot(view.Right, remainder), -Vector3.Dot(view.Up, remainder),
                -view.Distance - Vector3.Dot(view.Normal, remainder), 1));
        float far = view.Distance * 12;
        Matrix4 clip = view.Orthographic
            ? Matrix4.CreateOrthographic(size.X / view.Scale, size.Y / view.Scale, -far, far)
            : Matrix4.CreatePerspectiveFieldOfView(MathF.PI / 4, size.X / size.Y, view.Distance * .025f, far);
        return new(camera * clip, origin, GroundCoverage(view, tile, far), view.Right, view.Up, tile);
    }

    private FloorRegion GroundCoverage(Projection view, float tile, float far)
    {
        // The floor lies on XY. Intersect the frustum edges with that plane so that
        // orbiting through a large chart keeps using the existing spatial index.
        Span<Vector3> corners = stackalloc Vector3[8];
        float near = view.Orthographic ? -far : view.Distance * .025f;
        for (int i = 0; i < corners.Length; i++)
        {
            float depth = i < 4 ? near : far;
            float scale = (view.Orthographic ? 1 : depth / view.Distance) / view.Scale;
            corners[i] = view.Normal * (view.Distance - depth) +
                view.Right * (view.HalfSize.X * scale * ((i & 1) == 0 ? -1 : 1)) +
                view.Up * (view.HalfSize.Y * scale * ((i & 2) == 0 ? -1 : 1));
        }

        FloorRegion region = FloorRegion.Empty;
        float plane = (float)-Pivot.Z;
        for (int i = 0; i < corners.Length; i++)
        {
            if (corners[i].Z == plane)
                Include(corners[i]);
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((i & bit) != 0) continue;
                Vector3 a = corners[i], b = corners[i | bit];
                if ((a.Z < plane) != (b.Z < plane))
                    Include(Vector3.Lerp(a, b, (plane - a.Z) / (b.Z - a.Z)));
            }
        }
        if (region.Min.X > region.Max.X)
        {
            Vector2 center = new((float)Pivot.X, (float)Pivot.Y);
            region = new FloorRegion(center, center);
        }
        return region.Expand(new Vector2(tile));

        void Include(Vector3 point)
        {
            Vector2 world = new((float)(Pivot.X + point.X), (float)(Pivot.Y + point.Y));
            region = region.Union(new FloorRegion(world, world));
        }
    }

    internal readonly record struct Projection(Vector3 Right, Vector3 Up, Vector3 Normal, Vector2 HalfSize,
        float Distance, float Scale, bool Orthographic)
    {
        public float Depth(Vector3 point) => Distance - Vector3.Dot(point, Normal);
        private float Near => Distance * .025f;

        public Vector2 Project(Vector3 point)
        {
            float scale = Orthographic ? Scale : Scale * Distance / Math.Max(Near, Depth(point));
            return HalfSize + new Vector2(Vector3.Dot(point, Right), -Vector3.Dot(point, Up)) * scale;
        }

        public bool ContainsDepth(Vector3 point) => Orthographic || Depth(point) >= Near;

        public bool ClipLine(ref Vector3 a, ref Vector3 b)
        {
            bool firstInside = ContainsDepth(a);
            bool secondInside = ContainsDepth(b);
            if (!firstInside && !secondInside) return false;
            if (!firstInside) a = IntersectNear(a, b);
            else if (!secondInside) b = IntersectNear(b, a);
            return true;
        }

        public int ClipPolygon(ReadOnlySpan<Vector3> input, Span<Vector2> result)
        {
            int count = 0;
            Vector3 last = input[^1];
            bool lastInside = ContainsDepth(last);
            foreach (var point in input)
            {
                bool inside = ContainsDepth(point);
                if (inside != lastInside)
                    result[count++] = Project(IntersectNear(last, point));
                if (inside)
                    result[count++] = Project(point);
                last = point;
                lastInside = inside;
            }
            return count;
        }

        private Vector3 IntersectNear(Vector3 a, Vector3 b)
        {
            float depth = Depth(a);
            return Vector3.Lerp(a, b, (Near - depth) / (Depth(b) - depth));
        }
    }
}
