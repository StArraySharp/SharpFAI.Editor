using System.Buffers;
using System.Collections.Concurrent;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using System.Runtime.InteropServices;
using Thrust.Geometry;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal readonly record struct FloorVariant(FloorMaterial Material, System.Numerics.Vector2[] Outline, int Batch, float Radius = 0f);

internal sealed class FloorGeometry(
    Mesh mesh,
    IReadOnlyList<FloorTextureBatch> batches,
    Vector2[] centers,
    FloorVariant[] primary,
    FloorVariant[]?[] alternates,
    Aabb bounds,
    FloorStreamFrame? stream,
    int[] indexStarts,
    bool primaryPooled) : IDisposable
{
    private FloorVariant[] _primary = primary;

    public Mesh Mesh { get; } = mesh;
    public IReadOnlyList<FloorTextureBatch> Batches { get; } = batches;
    public Vector2[] Centers { get; } = centers;
    public Aabb Bounds { get; } = bounds;
    public FloorStreamFrame? Stream { get; } = stream;
    public int[] IndexStarts { get; } = indexStarts;
    public bool IsLevelStream { get; init; }
    public int IndexCount => Stream?.IndexCount ?? Mesh.IndexCount;
    public ReadOnlySpan<FloorVariant> GetVariants(int floor) =>
        alternates[floor] is { } variants ? variants : _primary.AsSpan(floor, 1);

    public void Dispose()
    {
        Stream?.Dispose();
        Mesh.Dispose();
        if (primaryPooled && _primary.Length != 0)
        {
            ArrayPool<FloorVariant>.Shared.Return(_primary, clearArray: true);
            _primary = [];
        }
    }
}

/// <summary>Bakes local floor geometry into one index stream in descending floor order.</summary>
internal static unsafe class OfficialFloorMeshBaker
{
    private const float UnitScale = 100f;
    private const int ShapeCacheLimit = 4096;

    private static readonly VertexDeclaration Layout = new(
        (VertexAttribute.Position, VertexFormat.Float4),
        (VertexAttribute.Normal, VertexFormat.Float4),
        (VertexAttribute.TexCoord0, VertexFormat.Float2),
        (VertexAttribute.Tangent, VertexFormat.Float4),
        (VertexAttribute.Color0, VertexFormat.Float4),
        (VertexAttribute.TexCoord1, VertexFormat.Float2),
        (VertexAttribute.Custom0, VertexFormat.Float4),
        (VertexAttribute.Custom1, VertexFormat.Float4),
        (VertexAttribute.Custom2, VertexFormat.Float4));

    // The field order matches Layout so each vertex can be written in one pass.
    [StructLayout(LayoutKind.Sequential)]
    private struct FloorVertex
    {
        public Vector4 Position;
        public Vector4 Icon;
        public Vector2 EdgeUv;
        public Vector4 Style;
        public Vector4 Color;
        public Vector2 TileUv;
        public Vector4 Shadow;
        public Vector4 Textures;
        public Vector4 TextureOffset;
    }

    internal readonly record struct Shape(double Entry, double Exit, float Width, float Length, int Curvature, bool Sprite)
    {
        public static Shape Create(Floor floor, FloorMaterial material) => new(floor.entryAngle, floor.exitAngle,
            material.Style.TileWidth * material.Dimensions.X,
            material.Style.TileLength * material.Dimensions.Y * material.LengthRatio,
            floor.isMidspin ? 3 : 40, material.Style.Sprite);

        // The per-level key already stores the complete shape. The string cache rounds angles and
        // can make a rebuilt viewport pick a different source angle after that cache is cleared.
        public FloorMeshResult Build() => new FloorMesh(Entry, Exit, Width, Length, Curvature, Sprite).Generate();
    }

    internal sealed class BakeCache(int shapeCacheLimit = ShapeCacheLimit)
    {
        private readonly ConcurrentDictionary<Shape, FloorMeshResult> _shapes = new();
        private readonly ConcurrentDictionary<TrackStyleInfo, (FloorTexture Edge, FloorTexture Perlin, FloorTexture Tile)>
            _styleTextures = new(ReferenceEqualityComparer.Instance);
        internal readonly ConcurrentDictionary<string, string?> TexturePaths = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentDictionary<string, FloorTexture> OutlineTextures = new(StringComparer.Ordinal);

        public (FloorTexture Edge, FloorTexture Perlin, FloorTexture Tile) GetStyleTextures(TrackStyleInfo style) =>
            _styleTextures.GetOrAdd(style, static value =>
            {
                string edge = value.EdgeResolved;
                FloorTexture border = value.EdgeTextureSuffix switch
                {
                    "rs13.png" => new(edge, Wrap: FloorTextureWrap.Mirror, Filter: FloorTextureFilter.Bilinear),
                    "basic_edge.png" or "minimal_edge.png" => new(edge, Filter: FloorTextureFilter.Point),
                    _ => new(edge, Filter: FloorTextureFilter.Bilinear),
                };
                FloorTexture perlin = value.PerlinResolved is string noise ? MirroredMipmapped(noise) : default;
                FloorTexture tile = value.TileResolved is not string surface ? default
                    : value.Sprite ? new(surface, FlipVertically: false, Filter: FloorTextureFilter.Bilinear)
                    : MirroredMipmapped(surface);
                return (border, perlin, tile);
            });

        private static FloorTexture MirroredMipmapped(string resource) =>
            new(resource, Wrap: FloorTextureWrap.Mirror, FlipVertically: false,
                Filter: FloorTextureFilter.BilinearMipmapped);

        public FloorMeshResult GetShape(Shape shape)
        {
            if (_shapes.TryGetValue(shape, out FloorMeshResult? result))
            {
                return result;
            }
            return _shapes.Count < shapeCacheLimit
                ? _shapes.GetOrAdd(shape, static key => key.Build()) : shape.Build();
        }
    }

    public static FloorGeometry BakeLevel(
        IReadOnlyList<Floor> floors,
        IReadOnlyList<FloorTrackState> states,
        int textureSlots,
        CancellationToken token = default,
        string? levelDirectory = null,
        IReadOnlyList<HashSet<FloorMaterial>?>? materialOptions = null,
        TrackStyleInfo? styleOverride = null,
        int[]? floorIndices = null,
        BakeCache? cache = null,
        bool computeBounds = true,
        FloorShapeLibrary? shapes = null,
        (FloorTexture Icon, FloorTexture Outline)? iconTextures = null)
    {
        FloorTextureBatchBuilder batches = new(textureSlots);
        int count = floorIndices?.Length ?? floors.Count;
        Vector2[] centers = new Vector2[count];
        int[] indexStarts = new int[count];
        FloorVariant[]?[] alternates = new FloorVariant[count][];
        FloorTexture[] textures = new FloorTexture[5];
        Span<int> slots = stackalloc int[5];
        cache ??= new BakeCache();
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        System.Diagnostics.Debug.Assert(sizeof(FloorVertex) == Layout.Stride);
        int vertexCapacity = 0, indexCapacity = 0, instanceCapacity = 0;
        if (floorIndices is not null || shapes is not null)
        {
            for (int local = 0; local < count; local++)
            {
                token.ThrowIfCancellationRequested();
                int i = floorIndices?[local] ?? local;
                FloorMaterial material = states[i].Material;
                ReserveShape(floors[i], material);
                if (materialOptions?[i] is { } options)
                {
                    foreach (FloorMaterial option in options)
                    {
                        if (option != material)
                        {
                            ReserveShape(floors[i], option);
                        }
                    }
                }
            }
        }
        Mesh mesh = new(Layout, vertexCapacity, IndexFormat.UInt32, shapes is null ? indexCapacity : 0)
            { Name = "OfficialFloorMesh" };
        FloorStreamFrame? stream = null;
        FloorVariant[] primary = [];
        bool poolPrimary = shapes is not null && count > 0;
        try
        {
            primary = poolPrimary ? ArrayPool<FloorVariant>.Shared.Rent(count) : new FloorVariant[count];
            if (shapes is not null)
            {
                stream = new FloorStreamFrame(shapes, indexCapacity, instanceCapacity);
            }
            for (int local = count - 1; local >= 0; local--)
            {
                token.ThrowIfCancellationRequested();
                indexStarts[local] = stream?.IndexCount ?? mesh.IndexCount;
                int i = floorIndices?[local] ?? local;
                Floor floor = floors[i];
                FloorTrackState state = states[i];
                Vector2 center = new(floor.position.X, floor.position.Y);
                centers[local] = center;
                FloorMaterial first = state.Material;
                if (styleOverride is not null)
                {
                    first = first with { Style = styleOverride };
                }

                FloorMaterial[]? choices = null;
                if (materialOptions?[i] is { Count: > 0 } options)
                {
                    choices = new[] { first }.Concat(options.Select(option => styleOverride is null
                        ? option : option with { Style = styleOverride })).Distinct().ToArray();
                }

                Span<FloorVariant> baked = choices is { Length: > 1 }
                    ? (alternates[local] = new FloorVariant[choices.Length])
                    : primary.AsSpan(local, 1);
                for (int variant = 0; variant < baked.Length; variant++)
                {
                    token.ThrowIfCancellationRequested();
                    FloorMaterial material = choices?[variant] ?? first;
                    TrackStyleInfo style = material.Style;
                    Shape shape = Shape.Create(floor, material);
                    FloorShapeLibrary.Shape template = shapes is null ? default : shapes[shape];
                    FloorMeshResult result = template.Mesh ?? cache.GetShape(shape);
                    if (result.Indices.Length == 0)
                    {
                        baked[variant] = new FloorVariant(material, result.Collider, -1);
                        continue;
                    }

                    (FloorTexture edge, FloorTexture perlin, FloorTexture tile) = cache.GetStyleTextures(style);
                    textures[0] = edge;
                    textures[1] = perlin;
                    string? custom = style.Sprite ? null : ResolveTexture(material.TextureName, levelDirectory, cache.TexturePaths);
                    textures[2] = custom is not null ? new FloorTexture(custom, Wrap: FloorTextureWrap.Repeat, External: true) : tile;
                    textures[3] = iconTextures?.Icon ?? FloorIconTextures.Resolve(floor, material);
                    FloorIcon? icon = material.HideIcon ? null : material.Icon ?? floor.icon;
                    textures[4] = iconTextures?.Outline ??
                        FloorIconTextures.ResolveOutline(textures[3], icon, levelDirectory, cache.OutlineTextures);
                    batches.AddFloor(textures, slots, stream?.IndexCount ?? mesh.IndexCount);
                    baked[variant] = new FloorVariant(material, result.Collider, batches.Batches.Count, template.Radius);

                    Vector4 textureIds = new(slots[0], slots[1], slots[2], slots[3]);
                    float marker = icon == FloorIcon.Vfx ? 1f : 0f;
                    Vector4 styleData = new(style.ShadowTune, style.Sprite ? 1f : 0f, material.IconOutline ? 1f : 0f, marker);
                    float tileScale = custom is null || Math.Abs(material.TextureScale) < 1e-6f
                        ? style.TileScale : material.TextureScale;
                    float iconAngle = floor.iconAngle + (material.ReverseIcon ? MathF.PI : 0f);
                    Vector4 iconData = new(iconAngle, variant, floor.iconFlipped ? 1f : 0f, tileScale);
                    Vector4 offset = new(floor.textureOffsetX, floor.textureOffsetY, floor.textureRotation, slots[4]);
                    Vector4 shadow = material.ShadowColor ?? style.ShadowColor;

                    if (stream is not null)
                    {
                        stream.Append(new FloorStreamInstance(new Vector4(template.Texel, local, 0f, 0f),
                            new FloorVertexProperties(iconData, styleData, shadow, textureIds, offset)), result.Indices);
                        continue;
                    }

                    FloorRenderTransform boundsTransform = state.Transform;
                    (float sin, float cos) = computeBounds ? MathF.SinCos(boundsTransform.Rotation) : default;

                    int baseVertex = mesh.AppendVertices(result.Vertices.Length);
                    Span<FloorVertex> packed = new((FloorVertex*)mesh.VertexPointer + baseVertex, result.Vertices.Length);
                    FloorVertex vertex = new() { Icon = iconData, Style = styleData, Shadow = shadow,
                        Textures = textureIds, TextureOffset = offset };
                    for (int v = 0; v < packed.Length; v++)
                    {
                        System.Numerics.Vector3 position = result.Vertices[v];
                        if (computeBounds)
                        {
                            float x = position.X * boundsTransform.Scale.X;
                            float y = position.Y * boundsTransform.Scale.Y;
                            Vector2 worldXY = center + boundsTransform.Offset + new Vector2(cos * x - sin * y, sin * x + cos * y);
                            Vector3 world = new(worldXY.X, worldXY.Y, position.Z);
                            min = Vector3.ComponentMin(min, world);
                            max = Vector3.ComponentMax(max, world);
                        }

                        System.Drawing.Color color = result.Colors[v];
                        System.Numerics.Vector2 uv = result.Uv0[v];
                        System.Numerics.Vector2 localUv = result.Uv1[v];
                        vertex.Position = new Vector4(position.X, position.Y, position.Z, local);
                        vertex.EdgeUv = new Vector2(uv.X, uv.Y);
                        vertex.Color = new Vector4(color.R, color.G, color.B, color.A) / 255f;
                        vertex.TileUv = new Vector2(localUv.X / UnitScale, localUv.Y / UnitScale);
                        packed[v] = vertex;
                    }

                    // FloorMesh already emits the body followed by both shadow strips.
                    mesh.AppendIndices(result.Indices, baseVertex);
                }
            }

            batches.Finish(stream?.IndexCount ?? mesh.IndexCount);
            if (shapes is null)
            {
                mesh.AddSubMeshForAll();
            }
            Aabb bounds = computeBounds && mesh.VertexCount > 0 ? new Aabb(min, max) : new Aabb(Vector3.Zero, Vector3.Zero);
            return new FloorGeometry(mesh, batches.Batches, centers, primary, alternates, bounds, stream, indexStarts,
                primaryPooled: poolPrimary) { IsLevelStream = shapes is not null && floorIndices is null };
        }
        catch
        {
            stream?.Dispose();
            mesh.Dispose();
            if (poolPrimary && primary.Length != 0)
                ArrayPool<FloorVariant>.Shared.Return(primary, clearArray: true);
            throw;
        }

        void ReserveShape(Floor floor, FloorMaterial material)
        {
            if (styleOverride is not null)
            {
                material = material with { Style = styleOverride };
            }
            Shape shape = Shape.Create(floor, material);
            FloorMeshResult result = shapes is null ? cache.GetShape(shape) : shapes[shape].Mesh!;
            if (result.Indices.Length > 0)
            {
                if (shapes is null)
                {
                    vertexCapacity += result.Vertices.Length;
                }
                indexCapacity += result.Indices.Length;
                instanceCapacity++;
            }
        }
    }

    private static string? ResolveTexture(string? name, string? directory, ConcurrentDictionary<string, string?> paths)
    {
        if (string.IsNullOrWhiteSpace(name) || directory is null)
        {
            return null;
        }

        if (paths.TryGetValue(name, out string? path))
        {
            return path;
        }

        try
        {
            path = Path.GetFullPath(name, directory);
            paths[name] = File.Exists(path) ? path : null;
        }
        catch (ArgumentException)
        {
            paths[name] = null;
        }

        return paths[name];
    }
}
