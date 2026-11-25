using System.Runtime.InteropServices;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;

namespace SharpFAI.Player.Tests;

public sealed class TestPositionTrackGuides
{
    [Test]
    public void ConnectorStartsAtPreviousTileEdgeAndDistinguishesEditorOffsets()
    {
        Floor[] floors = [new(0, 180, default), new(0, 180, default), new(0, 180, default)];
        PlaybackEvent[] events =
        [
            new PositionTrack { Floor = 1, PositionOffset = new(1, 0), EditorOnly = true },
            new PositionTrack { Floor = 1, PositionOffset = new(0, 1) },
            new PositionTrack { Floor = 2, PositionOffset = new(1, 0), JustThisTile = true },
        ];
        using var mesh = PositionTrackGuides.Build(floors, new FloorTrackState[floors.Length], [], events, 150)!;
        Assert.That(mesh.IndexCount, Is.EqualTo(12));
        (byte[] vertices, _) = mesh.ToArrays();
        Vector4 AtColor(int index) => MemoryMarshal.Read<Vector4>(vertices.AsSpan(mesh.Stride * index + 16));
        Vector2 AtPosition(int index) => MemoryMarshal.Read<Vector2>(vertices.AsSpan(mesh.Stride * index));

        Assert.That((AtPosition(0) + AtPosition(1)) * .5f, Is.EqualTo(new Vector2(75, 0)));
        Assert.That((AtPosition(4) + AtPosition(5)) * .5f, Is.EqualTo(new Vector2(225, 0)));
        Assert.That(AtColor(0), Is.EqualTo(new Vector4(.4f, 1f, .4f, 1f)));
        Assert.That(AtColor(4), Is.EqualTo(new Vector4(.75f, .5f, 1f, 1f)));
    }
}
