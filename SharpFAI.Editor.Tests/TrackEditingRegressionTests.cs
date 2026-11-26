using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor;
using SharpFAI.Player.Engine;
using SharpFAI.Framework;
using SharpFAI.Util;

namespace SharpFAI.Editor.Tests;

[TestFixture]
public sealed class TrackEditingRegressionTests
{
    private static EditorDocument Chart(int length = 6)
    {
        EditorDocument document = new();
        if (document.LastFloor > 0) document.DeleteTiles(1, document.LastFloor);
        document.InsertTiles(0, Enumerable.Repeat(0d, length).ToArray());
        return document;
    }

    private static JObject Add(EditorDocument document, int floor, string start, string end)
    {
        document.PasteEvents(floor, [new JObject
        {
            ["eventType"] = "MoveTrack", ["startTile"] = JArray.Parse(start), ["endTile"] = JArray.Parse(end),
        }], false);
        return document.Events(floor).Last();
    }

    private static string State(EditorDocument document) => document.SnapshotForPlayback().root.ToString();

    [Test]
    public void TileRotationTransformsTheThreeSupportedEventPositionsInOneUndo()
    {
        var document = Chart();
        document.PasteEvents(2, [JObject.Parse("{eventType:'PositionTrack', positionOffset:[1,2]}"),
            JObject.Parse("{eventType:'MoveTrack', positionOffset:[3,4]}"),
            JObject.Parse("{eventType:'MoveCamera', position:[5,6], zoom:120}"),
            JObject.Parse("{eventType:'MoveDecorations', positionOffset:[7,8]}")], false);
        document.PasteEvents(4, [JObject.Parse("{eventType:'MoveCamera', position:[9,10]}")], false);
        string before = State(document);

        var rotation = new TrackTransform(90);
        document.TransformTiles(2, 3, rotation.Angle, rotation);

        Assert.That(document.IncomingAngle(2), Is.EqualTo(90));
        var events = document.Events(2);
        Assert.That(events[0]["positionOffset"]!.Values<double>(), Is.EqualTo(new[] { -2d, 1d }));
        Assert.That(events[1]["positionOffset"]!.Values<double>(), Is.EqualTo(new[] { -4d, 3d }));
        Assert.That(events[2]["position"]!.Values<double>(), Is.EqualTo(new[] { -6d, 5d }));
        Assert.That(events[2].Value<int>("zoom"), Is.EqualTo(120));
        Assert.That(events[3]["positionOffset"]!.Values<double>(), Is.EqualTo(new[] { 7d, 8d }));
        Assert.That(document.Events(4)[0]["position"]!.Values<double>(), Is.EqualTo(new[] { 9d, 10d }));

        string after = State(document);
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
        document.Redo();
        Assert.That(State(document), Is.EqualTo(after));
    }

    [Test]
    public void FineRotationAndFlipsRespectMissingAxesAndTheSyncSwitch()
    {
        var document = Chart();
        document.PasteEvents(2, [JObject.Parse("{eventType:'MoveCamera', position:[2,null]}")], false);
        var move = document.Events(2).Single();
        var rotation = new TrackTransform(22.5);
        document.TransformTiles(2, 2, rotation.Angle);
        Assert.That(document.IncomingAngle(2), Is.EqualTo(22.5));
        Assert.That(JToken.DeepEquals(move["position"], JArray.Parse("[2,null]")), Is.True);

        var flip = TrackTransform.HorizontalFlip;
        document.TransformTiles(2, 2, flip.Angle, flip);
        Assert.That(document.IncomingAngle(2), Is.EqualTo(157.5));
        Assert.That(move["position"]![0]!.Value<double>(), Is.EqualTo(-2));
        Assert.That(move["position"]![1]!.Type, Is.EqualTo(JTokenType.Null));

        document.TransformTiles(2, 2, rotation.Angle, rotation);
        Assert.That(move["position"]![0]!.Value<double>(), Is.EqualTo(-2 * Math.Cos(Math.PI / 8)).Within(1e-9));
        Assert.That(move["position"]![1]!.Value<double>(), Is.EqualTo(-2 * Math.Sin(Math.PI / 8)).Within(1e-9));
    }

    [TestCase(1, 2, false, 1.25, 2.5)]
    [TestCase(1, 2, true, 0.5, 1)]
    [TestCase(1, 1, true, 0.5, 1)]
    [TestCase(1, 3, false, 1.25, 2.5)]
    [TestCase(3, 3, false, 0, 0)]
    public void SelectionTimingUsesStartingTempoAndTheOptionalEndpoint(int first, int last, bool excludeLast,
        double duration, double beats)
    {
        var stats = TrackSelectionStatistics.Calculate([-0.25, 0, 0.5, 1.25], [60, 120, 80, 80], first, last, excludeLast);
        Assert.That(stats, Is.Not.Null);
        Assert.That(stats!.Value.Duration, Is.EqualTo(duration));
        Assert.That(stats.Value.Beats, Is.EqualTo(beats));
        Assert.That(stats.Value.Angle, Is.EqualTo(beats * 180));
    }

    [Test]
    public void InsertRelocatesAbsoluteAndEndReferencesAndRestoresInOneUndo()
    {
        var document = Chart();
        var item = Add(document, 4, "[4, 'Start']", "[-1, 'End']");
        var relative = Add(document, 2, "[2, 'ThisTile']", "[0, 'End']");
        string before = State(document);
        document.InsertTiles(2, [45, 90]);
        Assert.That(EditorDocument.Floor(item), Is.EqualTo(6));
        Assert.That(item["startTile"]![0]!.Value<int>(), Is.EqualTo(6));
        Assert.That(item["endTile"]![0]!.Value<int>(), Is.EqualTo(-1));
        Assert.That(relative["startTile"]![0]!.Value<int>(), Is.EqualTo(2));
        string after = State(document);
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
        document.Redo();
        Assert.That(State(document), Is.EqualTo(after));
    }

    [Test]
    public void EndAnchorPreservesTargetBeforeInsertion()
    {
        var document = Chart();
        var item = Add(document, 0, "[1, 'Start']", "[-5, 'End']");
        document.InsertTiles(3, [0, 0]);
        Assert.That(item["endTile"]![0]!.Value<int>(), Is.EqualTo(-7));
        Assert.That(item["startTile"]![0]!.Value<int>(), Is.EqualTo(1));
    }

    [Test]
    public void DeleteClampsRemovedTargetsToSuccessorAndPreservesUndoRedo()
    {
        var document = Chart();
        var item = Add(document, 6, "[3, 'Start']", "[-2, 'End']");
        var relative = Add(document, 1, "[2, 'ThisTile']", "[6, 'Start']");
        string before = State(document);
        document.DeleteTiles(2, 4);
        Assert.That(EditorDocument.Floor(item), Is.EqualTo(3));
        Assert.That(item["startTile"]![0]!.Value<int>(), Is.EqualTo(2));
        Assert.That(item["endTile"]![0]!.Value<int>(), Is.EqualTo(-1));
        Assert.That(relative["startTile"]![0]!.Value<int>(), Is.EqualTo(2));
        string after = State(document);
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
        document.Redo();
        Assert.That(State(document), Is.EqualTo(after));
    }

    [Test]
    public void PasteRelocatesCopiedInternalReferencesAndUndoRestoresEverything()
    {
        var document = Chart();
        Add(document, 2, "[3, 'Start']", "[-3, 'End']");
        var clipboard = document.CopyTiles(2, 3);
        string before = State(document);
        document.PasteTiles(5, clipboard, true);
        JObject copy = document.Events(6).Single();
        Assert.That(copy["startTile"]![0]!.Value<int>(), Is.EqualTo(7));
        Assert.That(copy["endTile"]![0]!.Value<int>(), Is.EqualTo(-1));
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
    }

    [Test]
    public void SingleDragUpdatesExistingOffsetWithOneUndoAndPreservesFields()
    {
        var document = Chart();
        document.PasteEvents(2, [JObject.Parse("{eventType:'PositionTrack', positionOffset:[1,2], justThisTile:true, scale:[120,80]}")], false);
        string before = State(document);
        document.PositionTiles(2, 2, .5, -1);
        var item = document.Events(2).Single();
        Assert.That(item["positionOffset"]!.Values<double>(), Is.EqualTo(new[] { 1.5, 1d }));
        Assert.That(item["scale"]!.Values<int>(), Is.EqualTo(new[] { 120, 80 }));
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
    }

    [Test]
    public void RangeDragAddsCompensationUnlessSelectionReachesLastFloor()
    {
        var document = Chart();
        string before = State(document);
        document.PositionTiles(1, 3, 2, -1);
        Assert.That(document.Events(1).Single()["justThisTile"]!.Value<bool>(), Is.False);
        Assert.That(document.Events(4).Single()["positionOffset"]!.Values<double>(), Is.EqualTo(new[] { -2d, 1 }));
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
        document.Redo();
        document.PositionTiles(5, 6, 1, 1);
        Assert.That(document.Events(7), Is.Empty);
    }

    [Test]
    public void DisabledOffsetDoesNotLeakIntoNewDrag()
    {
        var document = Chart();
        document.PasteEvents(2, [JObject.Parse("{eventType:'PositionTrack', positionOffset:[99,99], justThisTile:true, disabled:{positionOffset:true}}")], false);
        string before = State(document);
        document.PositionTiles(2, 2, 1, 2);
        JObject item = document.Events(2).Single();
        Assert.That(item["positionOffset"]!.Values<double>(), Is.EqualTo(new[] { 1d, 2 }));
        Assert.That(item["disabled"]!["positionOffset"]!.Value<bool>(), Is.False);
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
    }

    [Test]
    public void BackwardDirectionProtectsMidspinsAndExistingFullTurns()
    {
        var document = Chart(0);
        document.InsertTiles(0, [0, 0, 999, 90, 270, 90]);
        Assert.Multiple(() =>
        {
            Assert.That(document.IsTileBacktrack(0, 180), Is.True);
            Assert.That(document.IsTileBacktrack(1, 180), Is.True);
            Assert.That(document.IsTileBacktrack(1, 179), Is.False);
            Assert.That(document.IsTileBacktrack(2, 180), Is.False);
            Assert.That(document.IsTileBacktrack(3, 0), Is.True);
            Assert.That(document.IsTileBacktrack(4, 270), Is.False);
            Assert.That(document.IsTileBacktrack(6, 270), Is.True);
            Assert.That(document.FullSpinAngle(0), Is.Null);
            Assert.That(document.FullSpinAngle(3), Is.EqualTo(999));
        });
    }

    [Test]
    public void FullTurnTakesTwoBeatsAndPlainTailDeletionKeepsTheTrackPatch()
    {
        var document = Chart(0);
        document.InsertTiles(0, [0]);
        int version = document.PlaybackVersion;
        var before = document.SnapshotForPlayback();
        document.InsertTiles(1, [document.FullSpinAngle(1)!.Value]);
        var timing = FloorTimingCalculator.Calculate(document.SnapshotForPlayback());
        double beat = 60 / document.Settings.Value<double>("bpm");
        Assert.That(timing[2].EntryTime - timing[1].EntryTime, Is.EqualTo(2 * beat).Within(1e-6));
        Assert.That(document.IsTileBacktrack(2, 0), Is.True);
        document.DeleteTiles(2, 2);
        var patch = document.PlaybackPatchSince(version)!;
        Assert.That(patch.PreservesBindings, Is.True);
        patch.Apply(before.root);
        Assert.That(JToken.DeepEquals(before.root, document.SnapshotForPlayback().root), Is.True);
    }

    [Test]
    public void DragRangeActuallyMovesOnlySelectedPlayerFloorStates()
    {
        var document = Chart();
        document.PositionTiles(2, 4, 1, -2);
        Floor[] floors = Enumerable.Range(0, 7).Select(index =>
            new Floor(0, 180, new System.Numerics.Vector2(index * Floor.length * 2, 0)) { index = index, bpm = 120 }).ToArray();
        var states = FloorTrackStateResolver.Resolve(document.SnapshotForPlayback(), floors);
        for (int index = 0; index < states.Length; index++)
        {
            float factor = index is >= 2 and <= 4 ? Floor.length * 2 : 0;
            Assert.That(states[index].Transform.Offset.X, Is.EqualTo(factor).Within(1e-6), $"floor {index}");
            Assert.That(states[index].Transform.Offset.Y, Is.EqualTo(-factor * 2).Within(1e-6), $"floor {index}");
        }
    }

    [Test]
    public void DecorationAnchorsAndReferencesMoveAndUndoTogether()
    {
        var document = Chart();
        document.PasteDecorations([JObject.Parse("{eventType:'AddDecoration', floor:4, startTile:[4,'Start'], endTile:[-2,'End']}")]);
        JObject decoration = (JObject)document.Decorations.Last!;
        string before = State(document);
        document.InsertTiles(2, [0]);
        Assert.That(EditorDocument.Floor(decoration), Is.EqualTo(5));
        Assert.That(decoration["startTile"]![0]!.Value<int>(), Is.EqualTo(5));
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
        document.DeleteTiles(3, 6);
        Assert.That(EditorDocument.Floor(decoration), Is.EqualTo(2));
        Assert.That(decoration["startTile"]![0]!.Value<int>(), Is.EqualTo(2));
        document.Undo();
        Assert.That(State(document), Is.EqualTo(before));
    }

    [Test]
    public void LongChartRepeatedInsertionKeepsReferencesAndUndoStable()
    {
        var document = Chart(10000);
        var item = Add(document, 10000, "[10000,'Start']", "[0,'End']");
        int version = document.ChangeVersion;
        for (int i = 0; i < 100; i++) document.InsertTiles(5000 + i, [45]);
        Assert.That(document.LastFloor, Is.EqualTo(10100));
        Assert.That(item["startTile"]![0]!.Value<int>(), Is.EqualTo(10100));
        Assert.That(document.ChangeVersion - version, Is.EqualTo(100));
        for (int i = 0; i < 100; i++) document.Undo();
        Assert.That(document.LastFloor, Is.EqualTo(10000));
        Assert.That(EditorDocument.Floor(item), Is.EqualTo(10000));
    }

    [TestCase(75, 120, 90)]
    [TestCase(100, 120, 120)]
    [TestCase(150, 120, 180)]
    public void PlaybackSnapshotCombinesRateWithoutChangingDocumentPitch(int percent, double pitch, double expected)
    {
        var document = Chart();
        document.Set(document.Settings, "pitch", new JValue(pitch));
        var snapshot = document.SnapshotForPlayback();
        snapshot.PutSetting("pitch", pitch * EditorCanvasInput.PlaybackMultiplier(percent, PlaybackRate.Configured));
        Assert.That(snapshot.root["settings"]!["pitch"]!.Value<double>(), Is.EqualTo(expected));
        Assert.That(document.Settings["pitch"]!.Value<double>(), Is.EqualTo(pitch));
    }
}
