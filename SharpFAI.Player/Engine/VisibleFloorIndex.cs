using OpenTK.Mathematics;
using SharpFAI.Framework;
using Thrust.Geometry;
using Thrust.Scene;

namespace SharpFAI.Player.Engine;

internal readonly record struct FloorTimeRange(double Start, double End)
{
    public static FloorTimeRange All => new(double.NegativeInfinity, double.PositiveInfinity);
    public static FloorTimeRange Empty => new(double.PositiveInfinity, double.NegativeInfinity);
    public bool Contains(double time) => Start <= time && time <= End;
    public bool Intersects(FloorTimeRange other) => Start <= other.End && other.Start <= End;
    public FloorTimeRange Union(FloorTimeRange other) => new(Math.Min(Start, other.Start), Math.Max(End, other.End));
}

internal readonly record struct FloorRegion(Vector2 Min, Vector2 Max)
{
    public static FloorRegion Empty => new(new Vector2(float.PositiveInfinity), new Vector2(float.NegativeInfinity));

    public FloorRegion Union(FloorRegion other) => new(Vector2.ComponentMin(Min, other.Min), Vector2.ComponentMax(Max, other.Max));

    public FloorRegion Expand(Vector2 amount) => new(Min - amount, Max + amount);

    public bool Contains(FloorRegion other) =>
        Min.X <= other.Min.X && Min.Y <= other.Min.Y && Max.X >= other.Max.X && Max.Y >= other.Max.Y;

    public bool Intersects(FloorRegion other) =>
        Min.X <= other.Max.X && Max.X >= other.Min.X && Min.Y <= other.Max.Y && Max.Y >= other.Min.Y;

    public static FloorRegion FromView(Camera2D camera)
    {
        Vector2 x = camera.ScreenToWorld(new Vector2(camera.ViewportWidth, 0f)) - camera.Position;
        Vector2 y = camera.ScreenToWorld(Vector2.Zero) - camera.Position;
        Vector2 extent = Vector2.ComponentMax(new Vector2(MathF.Abs(x.X), MathF.Abs(x.Y)),
            new Vector2(MathF.Abs(y.X), MathF.Abs(y.Y)));
        return new FloorRegion(camera.Position - extent, camera.Position + extent);
    }
}

/// <summary>Conservative bounds for each tile, grouped in chart order for cheap viewport queries.</summary>
internal sealed class VisibleFloorIndex
{
    public const int MinimumFloorCount = 8192;
    public const int MinimumResidentFloorCount = 4096;
    public const int FloorsPerGroup = 256;
    private readonly FloorRegion[] _regions;
    private readonly FloorRegion[] _groups;
    private readonly FloorTimeRange[] _floorTimes;
    private readonly FloorTimeRange[] _groupTimes;

    private VisibleFloorIndex(FloorRegion[] regions, FloorRegion[] groups, FloorTimeRange[] floorTimes,
        FloorTimeRange[] groupTimes, FloorRegion bounds, FloorShapeLibrary shapes)
    {
        _regions = regions;
        _groups = groups;
        _floorTimes = floorTimes;
        _groupTimes = groupTimes;
        Shapes = shapes;
        Bounds = new Aabb(new Vector3(bounds.Min.X, bounds.Min.Y, 0f), new Vector3(bounds.Max.X, bounds.Max.Y, 0f));
    }

    public Aabb Bounds { get; }
    public FloorShapeLibrary Shapes { get; }

    public static VisibleFloorIndex Build(IReadOnlyList<Floor> floors, FloorTrackState[] preview,
        RuntimeTrackTimeline runtime, HashSet<FloorMaterial>?[] materials, CancellationToken token,
        TrackStyleInfo? styleOverride = null, FloorRenderTransform[]? overrides = null, FloorShapeLibrary? existingShapes = null)
    {
        FloorRegion[] regions = new FloorRegion[floors.Count];
        FloorRegion[] groups = new FloorRegion[(floors.Count + FloorsPerGroup - 1) / FloorsPerGroup];
        FloorTimeRange[] floorTimes = new FloorTimeRange[floors.Count];
        FloorTimeRange[] groupTimes = new FloorTimeRange[groups.Length];
        FloorShapeLibrary shapes = existingShapes ?? new FloorShapeLibrary();
        var moving = runtime.GetMotionBounds(token);
        FloorRegion bounds = FloorRegion.Empty;
        for (int group = 0; group < groups.Length; group++)
        {
            token.ThrowIfCancellationRequested();
            FloorRegion whole = FloorRegion.Empty;
            FloorTimeRange time = FloorTimeRange.Empty;
            int end = Math.Min((group + 1) * FloorsPerGroup, floors.Count);
            for (int i = group * FloorsPerGroup; i < end; i++)
            {
                FloorTrackState track = runtime.Baseline[i];
                FloorRenderTransform before = overrides?[i] ?? preview[i].Transform;
                FloorRenderTransform during = overrides?[i] ?? track.Transform;
                Vector2 center = Location(i);
                float radius = Radius(floors[i], preview[i].Material);
                if (materials[i] is { } choices)
                {
                    foreach (FloorMaterial choice in choices)
                    {
                        radius = MathF.Max(radius, Radius(floors[i], choice));
                    }
                }

                Vector2 anchor = center + during.Offset;
                FloorRegion region = new(anchor, anchor);
                Vector2 maximumScale = Vector2.ComponentMax(Vector2.Abs(before.Scale), Vector2.Abs(during.Scale));
                if (overrides is null)
                {
                    if (moving.TryGetValue(i, out var motion))
                    {
                        Vector2 margin = motion.Overshoots ? motion.MaxOffset - motion.MinOffset : Vector2.Zero;
                        region = new FloorRegion(anchor + motion.MinOffset - margin, anchor + motion.MaxOffset + margin);
                        Vector2 scaleMargin = motion.Overshoots ? motion.MaxScale - motion.MinScale : Vector2.Zero;
                        maximumScale = Vector2.ComponentMax(maximumScale, Vector2.ComponentMax(
                            Vector2.Abs(motion.MinScale - scaleMargin), Vector2.Abs(motion.MaxScale + scaleMargin)));
                    }
                    float spread = Floor.length * 2f * MathF.Abs(track.LengthRatio);
                    float appearSpread = track.Appear switch
                    {
                        TrackAnimation.Assemble => 4f * spread,
                        TrackAnimation.Assemble_Far => 8f * spread,
                        _ => 0f,
                    };
                    float disappearSpread = track.Disappear switch
                    {
                        TrackAnimation.Scatter => 4f * spread,
                        TrackAnimation.Scatter_Far => 8f * spread,
                        _ => 0f,
                    };
                    region = region.Expand(new Vector2(appearSpread + disappearSpread));
                    if (track.Appear is TrackAnimation.Drop or TrackAnimation.Rise)
                    {
                        region = region.Expand(new Vector2(0f, Floor.length * 16f));
                    }
                    if (track.Appear == TrackAnimation.Extend && i > 0)
                    {
                        Vector2 delta = Location(i - 1) - center;
                        region = region.Union(new FloorRegion(region.Min + delta, region.Max + delta));
                    }
                    if (track.Disappear == TrackAnimation.Retract && i + 1 < floors.Count)
                    {
                        Vector2 delta = Location(i + 1) - center;
                        region = region.Union(new FloorRegion(region.Min + delta, region.Max + delta));
                    }
                }

                Vector2 initial = center + before.Offset;
                float scale = MathF.Max(maximumScale.X, maximumScale.Y);
                regions[i] = region.Union(new FloorRegion(initial, initial)).Expand(new Vector2(radius * scale + 1f));
                whole = whole.Union(regions[i]);
                floorTimes[i] = runtime.VisibilityAt(i);
                time = time.Union(floorTimes[i]);
            }
            groups[group] = whole;
            groupTimes[group] = time;
            bounds = bounds.Union(whole);
        }

        return new VisibleFloorIndex(regions, groups, floorTimes, groupTimes, bounds, shapes);

        Vector2 Location(int i) => new(floors[i].position.X, floors[i].position.Y);

        float Radius(Floor floor, FloorMaterial material)
        {
            if (styleOverride is not null)
            {
                material = material with { Style = styleOverride };
            }
            return shapes.Add(OfficialFloorMeshBaker.Shape.Create(floor, material));
        }
    }

    public int[] Select(FloorRegion region) => Select(region, FloorTimeRange.All);

    public int[] Select(FloorRegion region, FloorTimeRange time)
    {
        List<int> visible = [];
        for (int group = 0; group < _groups.Length; group++)
        {
            if (!_groups[group].Intersects(region) || !_groupTimes[group].Intersects(time))
            {
                continue;
            }

            int end = Math.Min((group + 1) * FloorsPerGroup, _regions.Length);
            for (int i = group * FloorsPerGroup; i < end; i++)
            {
                if (_floorTimes[i].Intersects(time) && _regions[i].Intersects(region))
                {
                    visible.Add(i);
                }
            }
        }
        return visible.ToArray();
    }

    public int[] SelectGroups(FloorRegion region, FloorTimeRange time, int budget)
    {
        List<int> visible = [];
        for (int group = 0; group < _groups.Length && visible.Count <= budget; group++)
        {
            if (_groups[group].Intersects(region) && _groupTimes[group].Intersects(time))
            {
                int end = Math.Min((group + 1) * FloorsPerGroup, _regions.Length);
                for (int i = group * FloorsPerGroup; i < end; i++)
                {
                    if (_floorTimes[i].Intersects(time) && _regions[i].Intersects(region))
                    {
                        visible.Add(group);
                        break;
                    }
                }
            }
        }
        return visible.ToArray();
    }
}
