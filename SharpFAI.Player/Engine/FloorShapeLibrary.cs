using System.Buffers;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

/// <summary>Immutable after the visibility index is built. Each distinct silhouette is uploaded once.</summary>
internal sealed class FloorShapeLibrary
{
    public const int VertexBits = 10;
    public const int VerticesPerShape = 1 << VertexBits;
    private const int MaximumTexels = 8 * 1024 * 1024;

    internal readonly record struct Shape(FloorMeshResult? Mesh, int Texel, float Radius);

    private readonly Dictionary<OfficialFloorMeshBaker.Shape, Shape> _shapes = [];
    private readonly List<Vector4> _vertices = [];

    public bool Available { get; private set; } = true;
    public int TexelCount => _vertices.Count;
    public ReadOnlySpan<Vector4> Vertices => CollectionsMarshal.AsSpan(_vertices);
    public Shape this[OfficialFloorMeshBaker.Shape shape] => _shapes[shape];

    public float Add(OfficialFloorMeshBaker.Shape shape)
    {
        if (_shapes.TryGetValue(shape, out Shape existing))
        {
            return existing.Radius;
        }

        FloorMeshResult mesh = shape.Build();
        float squared = 0;
        foreach (var position in mesh.Vertices)
        {
            squared = MathF.Max(squared, position.X * position.X + position.Y * position.Y);
        }

        int texel = _vertices.Count;
        if (Available && mesh.Vertices.Length <= VerticesPerShape && texel + 3 * mesh.Vertices.Length <= MaximumTexels)
        {
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                var position = mesh.Vertices[i];
                var color = mesh.Colors[i];
                var edge = mesh.Uv0[i];
                var tile = mesh.Uv1[i];
                _vertices.Add(new Vector4(position.X, position.Y, position.Z, 0f));
                _vertices.Add(new Vector4(color.R, color.G, color.B, color.A) / 255f);
                _vertices.Add(new Vector4(edge.X, edge.Y, tile.X / 100f, tile.Y / 100f));
            }
        }
        else if (Available)
        {
            Available = false;
            _vertices.Clear();
            _vertices.TrimExcess();
        }

        float radius = MathF.Sqrt(squared);
        _shapes.Add(shape, new Shape(Available ? mesh : null, texel, radius));
        return radius;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct FloorVertexProperties(
    Vector4 Icon, Vector4 Style, Vector4 Shadow, Vector4 Textures, Vector4 TextureOffset);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct FloorStreamInstance(Vector4 Address, FloorVertexProperties Properties);

internal sealed class FloorStreamFrame : IDisposable
{
    public const int TexelsPerInstance = 6;

    private int[]? _indices;
    private FloorStreamInstance[]? _instances;

    public FloorStreamFrame(FloorShapeLibrary shapes, int indexCapacity, int instanceCapacity)
    {
        Shapes = shapes;
        _indices = ArrayPool<int>.Shared.Rent(indexCapacity);
        try
        {
            _instances = ArrayPool<FloorStreamInstance>.Shared.Rent(instanceCapacity);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public FloorShapeLibrary Shapes { get; }
    public int IndexCount { get; private set; }
    private int InstanceCount { get; set; }
    public ReadOnlySpan<int> Indices => _indices.AsSpan(0, IndexCount);
    public ReadOnlySpan<FloorStreamInstance> Instances => _instances.AsSpan(0, InstanceCount);

    public void Append(FloorStreamInstance instance, ReadOnlySpan<int> indices)
    {
        int firstVertex = checked(InstanceCount * FloorShapeLibrary.VerticesPerShape);
        _instances![InstanceCount++] = instance;
        Span<int> destination = _indices.AsSpan(IndexCount, indices.Length);
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = firstVertex + indices[i];
        }
        IndexCount += destination.Length;
    }

    public void Dispose()
    {
        if (_indices is { } indices)
        {
            ArrayPool<int>.Shared.Return(indices);
            _indices = null;
        }
        if (_instances is { } instances)
        {
            ArrayPool<FloorStreamInstance>.Shared.Return(instances);
            _instances = null;
        }
    }
}
