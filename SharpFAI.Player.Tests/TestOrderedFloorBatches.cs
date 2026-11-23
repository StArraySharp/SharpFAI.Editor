using OpenTK.Mathematics;
using SharpFAI.Player.Engine;
using SharpFAI.Framework;

namespace SharpFAI.Player.Tests;

public class TestOrderedFloorBatches
{
    [Test]
    public void ViewportBatchUsesLocalIdsAndKeepsDescendingChartOrder()
    {
        Floor[] floors =
        [
            new(0, 180, new System.Numerics.Vector2(-1000, 0)),
            new(0, 180, new System.Numerics.Vector2(50000, 0)),
            new(0, 180, new System.Numerics.Vector2(1000, 0)),
        ];
        FloorTrackState state = new(TrackStyleInfo.Standard, Vector4.One, Vector4.One,
            TrackColorMode.Single, 0, 1, 1, 0, 0, 0);
        FloorTrackState[] states = [state, state, state];
        RuntimeTrackTimeline runtime = new([], floors, states, [0, .5, 1], 1);
        HashSet<FloorMaterial>?[] materials = runtime.PrepareMaterials(CancellationToken.None);
        VisibleFloorIndex index = VisibleFloorIndex.Build(floors, states, runtime, materials, CancellationToken.None);
        int[] selected = index.Select(new FloorRegion(new Vector2(-1200), new Vector2(1200)));
        Assert.That(selected, Is.EqualTo(new[] { 0, 2 }));

        using FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, 16, floorIndices: selected);
        Assert.That(geometry.Centers.Select(center => center.X), Is.EqualTo(new[] { -1000f, 1000f }));
        (byte[] vertices, _) = geometry.Mesh.ToArrays();
        int stride = geometry.Mesh.Stride;
        int first = (int)geometry.Mesh.GetIndex(0), last = (int)geometry.Mesh.GetIndex(geometry.Mesh.IndexCount - 1);
        Assert.That(BitConverter.ToSingle(vertices, first * stride + 3 * sizeof(float)), Is.EqualTo(1f));
        Assert.That(BitConverter.ToSingle(vertices, last * stride + 3 * sizeof(float)), Is.Zero);
    }

    [Test]
    public void StreamSharesShapeVerticesAndPreservesTheOrderedIndexStream()
    {
        Floor[] floors =
        [
            new(0, 180, new System.Numerics.Vector2(-150, 0)),
            new(90, 180, default),
            new(0, 180, new System.Numerics.Vector2(150, 0)),
        ];
        FloorTrackState state = new(TrackStyleInfo.Standard, Vector4.One, Vector4.One,
            TrackColorMode.Single, 0, 1, 1, 0, 0, 0);
        FloorTrackState[] states = [state, state, state];
        FloorShapeLibrary shapes = new();
        var shape = OfficialFloorMeshBaker.Shape.Create(floors[0], state.Material);
        shapes.Add(shape);
        shapes.Add(OfficialFloorMeshBaker.Shape.Create(floors[2], state.Material));
        FloorMeshResult template = shapes[shape].Mesh!;

        using FloorGeometry expanded = OfficialFloorMeshBaker.BakeLevel(floors, states, 16, floorIndices: [0, 2]);
        using FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, 16,
            floorIndices: [0, 2], computeBounds: false, shapes: shapes);
        FloorStreamFrame frame = geometry.Stream!;
        Assert.That(shapes.TexelCount, Is.EqualTo(template.Vertices.Length * 3));
        Assert.That(frame.Instances[0].Address, Is.EqualTo(new Vector4(shapes[shape].Texel, 1, 0, 0)));
        Assert.That(frame.Instances[1].Address, Is.EqualTo(new Vector4(shapes[shape].Texel, 0, 0, 0)));
        Assert.That(frame.IndexCount, Is.EqualTo(expanded.IndexCount));
        Assert.That(geometry.Batches.Single().Textures, Is.EqualTo(expanded.Batches.Single().Textures));
        for (int i = 0; i < frame.IndexCount; i++)
        {
            int instance = frame.Indices[i] >> FloorShapeLibrary.VertexBits;
            int vertex = frame.Indices[i] & (FloorShapeLibrary.VerticesPerShape - 1);
            Assert.That(instance * template.Vertices.Length + vertex, Is.EqualTo((int)expanded.Mesh.GetIndex(i)));
        }
    }

    [Test]
    public void LevelStreamPreservesGlobalFloorIdsAndIndexRanges()
    {
        Floor[] floors =
        [
            new(0, 180, new System.Numerics.Vector2(-150, 0)),
            new(90, 180, default),
            new(0, 180, new System.Numerics.Vector2(150, 0)),
        ];
        FloorTrackState state = new(TrackStyleInfo.Standard, Vector4.One, Vector4.One,
            TrackColorMode.Single, 0, 1, 1, 0, 0, 0);
        FloorTrackState[] states = [state, state, state];
        FloorShapeLibrary shapes = new();
        foreach (Floor floor in floors)
            shapes.Add(OfficialFloorMeshBaker.Shape.Create(floor, state.Material));

        using FloorGeometry level = OfficialFloorMeshBaker.BakeLevel(floors, states, 16,
            computeBounds: false, shapes: shapes);
        FloorStreamFrame stream = level.Stream!;
        Assert.That(level.IsLevelStream, Is.True);
        Assert.That(stream.InstanceTexels, Is.EqualTo(floors.Length * FloorStreamFrame.TexelsPerInstance));
        for (int floor = floors.Length - 1; floor >= 0; floor--)
        {
            int first = level.IndexStarts[floor];
            int end = floor == 0 ? level.IndexCount : level.IndexStarts[floor - 1];
            Assert.That(end, Is.GreaterThan(first));
            int instance = stream.Indices[first] >> FloorShapeLibrary.VertexBits;
            Assert.That(stream.Instances[instance].Address.Y, Is.EqualTo(floor));
        }
    }

    [Test]
    public void TextureExhaustionDoesNotReorderOverlappingFloors()
    {
        Floor[] floors =
        [
            new(0, 180, default) { index = 0, icon = FloorIcon.Rabbit },
            new(0, 180, default) { index = 1, icon = FloorIcon.Snail },
            new(0, 180, default) { index = 2, icon = FloorIcon.Rabbit },
        ];
        FloorTrackState neon = new(TrackStyleInfo.Neon, Vector4.One, Vector4.One,
            TrackColorMode.Single, 0, 1, 1, 0, 0, float.PositiveInfinity) { IconOutline = true };
        FloorTrackState[] states = [neon, neon with { Style = TrackStyleInfo.Minimal }, neon];

        using FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, textureSlots: 3);
        Assert.That(geometry.Batches.Select(batch => batch.Textures.Length), Is.EqualTo(new[] { 3, 3, 3 }));

        (byte[] vertices, _) = geometry.Mesh.ToArrays();
        int nextIndex = 0;
        List<int> drawOrder = [];
        foreach (FloorTextureBatch batch in geometry.Batches)
        {
            Assert.That(batch.IndexStart, Is.EqualTo(nextIndex));
            int end = batch.IndexStart + batch.IndexCount;
            for (; nextIndex < end; nextIndex++)
            {
                int vertex = (int)geometry.Mesh.GetIndex(nextIndex);
                int floor = (int)BitConverter.ToSingle(vertices, vertex * geometry.Mesh.Stride + 3 * sizeof(float));
                if (drawOrder.Count == 0 || drawOrder[^1] != floor)
                {
                    drawOrder.Add(floor);
                }
            }
        }

        Assert.That(drawOrder, Is.EqualTo(new[] { 2, 1, 0 }));
        Assert.That(nextIndex, Is.EqualTo(geometry.Mesh.IndexCount));
    }

    [Test]
    public void RuntimeCullingRetainsFadingAndMovedFloors()
    {
        int count = VisibleFloorIndex.FloorsPerGroup + 1;
        Floor[] floors = Enumerable.Range(0, count).Select(i => new Floor(0, 180, default)
            { index = i, bpm = 120 }).ToArray();
        FloorTrackState fade = new(TrackStyleInfo.Standard, Vector4.One, Vector4.One,
            TrackColorMode.Single, 0, 1, 1, 0, 0, 0)
        {
            Appear = TrackAnimation.Fade,
            Disappear = TrackAnimation.Fade,
        };
        FloorTrackState[] states = Enumerable.Repeat(fade, count).ToArray();
        double[] entries = Enumerable.Range(0, count).Select(i => .5 * i).ToArray();
        RuntimeTrackTimeline runtime = new([new SharpFAI.Player.Engine.Events.MoveTrack
        {
            Floor = count - 1,
            PositionOffset = new SharpFAI.Player.Engine.Events.EventVector(20, null),
        }], floors, states, entries, 1);
        VisibleFloorIndex index = VisibleFloorIndex.Build(floors, states, runtime,
            runtime.PrepareMaterials(CancellationToken.None), CancellationToken.None);

        FloorRegion origin = new(new Vector2(-100), new Vector2(100));
        Assert.That(index.SelectGroups(origin, new FloorTimeRange(127.75, 127.9), 4), Is.EqualTo(new[] { 0 }));
        Assert.That(index.SelectGroups(origin, new FloorTimeRange(128.1, 128.2), 4), Is.EqualTo(new[] { 0, 1 }));
        Assert.That(index.SelectGroups(origin, new FloorTimeRange(128.3, 128.5), 4), Is.EqualTo(new[] { 1 }));
        Assert.That(index.SelectGroups(origin, FloorTimeRange.All, 4), Is.EqualTo(new[] { 0, 1 }));
        Vector2 moved = new(20 * 2f * Floor.length, 0);
        Assert.That(index.SelectGroups(new FloorRegion(moved - Vector2.One, moved + Vector2.One),
            new FloorTimeRange(129, 129.1), 4), Is.EqualTo(new[] { 1 }));
    }
}
