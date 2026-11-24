using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor.Tests;

public sealed class TimeSignatureTimelineTests
{
    [Test]
    public void FloorMarkersKeepTheSelectionAtSharedTimestampsAndRespectTheHitDistance()
    {
        double[] entries = [-1, 0, .5, .5, 1, 1.25];
        Assert.Multiple(() =>
        {
            Assert.That(TimelineFloorTimes.LowerBound(entries, 0), Is.EqualTo(1));
            Assert.That(TimelineFloorTimes.Nearest(entries, .5, .015, -1), Is.EqualTo(2));
            Assert.That(TimelineFloorTimes.Nearest(entries, .5, .015, 3), Is.EqualTo(3));
            Assert.That(TimelineFloorTimes.Nearest(entries, .7, .015, 3), Is.EqualTo(-1));
            Assert.That(TimelineFloorTimes.Nearest(entries, 1.26, .02, -1), Is.EqualTo(5));
        });
    }

    [Test]
    public void BeatClockFollowsThePreviewTempoAndHandlesSimultaneousFloors()
    {
        var clock = new EditorBeatClock([-2, 0, .5, 1, 1, 1.25], [120, 120, 120, 120, 240, 240], 120);
        Assert.Multiple(() =>
        {
            Assert.That(clock.BeatAt(-.5), Is.EqualTo(-1));
            Assert.That(clock.BeatAt(1.125), Is.EqualTo(2.5));
            Assert.That(clock.TimeAt(3), Is.EqualTo(1.25));
            Assert.That(clock.TimeAt(clock.BeatAt(2.75)), Is.EqualTo(2.75));
        });
    }

    [Test]
    public void DragScrollsTowardEitherEdgeAndStopsAtTheSongBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TimelineNavigation.DragScroll(4, 4, 12, .5, .15, .25), Is.EqualTo(4));
            Assert.That(TimelineNavigation.DragScroll(4, 4, 12, .925, .15, .25), Is.EqualTo(4.25).Within(1e-9));
            Assert.That(TimelineNavigation.DragScroll(4, 4, 12, .075, .15, .25), Is.EqualTo(3.75).Within(1e-9));
            Assert.That(TimelineNavigation.DragScroll(7.7, 4, 12, 1.2, .15, .25), Is.EqualTo(8));
            Assert.That(TimelineNavigation.DragScroll(.3, 4, 12, -.2, .15, .25), Is.Zero);
        });
    }

    [Test]
    public void PlayingFollowsThePlayheadBeforeItReachesEitherEdge()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TimelineNavigation.KeepPlayheadInView(10, 4, 20, 13.5, 13.4, .15, true),
                Is.EqualTo(10.1).Within(1e-9));
            Assert.That(TimelineNavigation.KeepPlayheadInView(10, 4, 20, 10.3, 10.8, .15, true),
                Is.EqualTo(9.7).Within(1e-9));
            Assert.That(TimelineNavigation.KeepPlayheadInView(10, 4, 20, 8, 7.9, .15, true),
                Is.EqualTo(7.4).Within(1e-9));
            Assert.That(TimelineNavigation.KeepPlayheadInView(10, 4, 20, 13.5, 13.4, .15, false), Is.EqualTo(10));
        });
    }

    [Test]
    public void ShiftSeekLandsOnAWholeBeatWithinTheSongAndPlainSeekKeepsThePointerTime()
    {
        var clock = new EditorBeatClock([-.5, .2], [120, 120], 120);
        var signatures = EditorTimeSignatures.Read(null).WithChange(2, new TimeSignature(6, 8));
        var offsetClock = new EditorBeatClock([-1, -.2], [120, 120], 120);
        Assert.Multiple(() =>
        {
            Assert.That(TimelineNavigation.SeekTime(1.32, 2, false, clock, signatures), Is.EqualTo(1.32));
            Assert.That(TimelineNavigation.SeekTime(1.34, 2, true, clock, signatures), Is.EqualTo(1.45).Within(1e-9));
            Assert.That(TimelineNavigation.SeekTime(1.39, 1.4, true, clock, signatures), Is.EqualTo(1.2).Within(1e-9));
            Assert.That(TimelineNavigation.SeekTime(.01, 2, true, offsetClock, signatures), Is.EqualTo(.3).Within(1e-9));
            Assert.That(signatures.PositionAt(2.5), Is.EqualTo((2d, 2)));
        });
    }

    [Test]
    public void ChangingMeterStartsANewBarAndChangesTheBeatSpacing()
    {
        var meters = EditorTimeSignatures.Read(null)
            .WithChange(3, new TimeSignature(6, 8))
            .WithChange(9, new TimeSignature(3, 4));
        Assert.Multiple(() =>
        {
            Assert.That(meters.MeasureAt(2.99), Is.EqualTo(1));
            Assert.That(meters.MeasureAt(3), Is.EqualTo(2));
            Assert.That(meters.MeasureAt(6), Is.EqualTo(3));
            Assert.That(meters.MeasureAt(9), Is.EqualTo(4));
            Assert.That(meters.Snap(3.3), Is.EqualTo(3.5));
            Assert.That(meters.Snap(8.9), Is.EqualTo(9));
            Assert.That(meters.Snap(8.9, previousOnly: true), Is.EqualTo(8.5));
            Assert.That(meters.WithChange(3, null).MeasureAt(6), Is.EqualTo(2));
        });
    }

    [Test]
    public void MeterEditsSaveAndUndoWithoutChangingThePlaybackRevision()
    {
        var directory = Directory.CreateTempSubdirectory("sharpfai-meter-");
        try
        {
            var document = new EditorDocument();
            var previous = document.SnapshotForPlayback();
            document.SetTimeSignature(0, new TimeSignature(3, 4));
            document.SetTimeSignature(9, new TimeSignature(7, 8));
            Assert.That(document.Modified, Is.True);
            Assert.That(document.PlaybackVersion, Is.Zero);
            document.Undo();
            Assert.That(document.TimeSignatures.Changes, Has.Count.EqualTo(1));
            document.Redo();
            Assert.That(document.TimeSignatures.Changes, Has.Count.EqualTo(2));
            Assert.That(document.PlaybackVersion, Is.Zero);

            string path = Path.Combine(directory.FullName, "level.adofai");
            document.Save(path);
            var reopened = new EditorDocument(path);
            Assert.That(reopened.TimeSignatures.Changes, Is.EqualTo(document.TimeSignatures.Changes));
            var saved = reopened.SnapshotForPlayback();
            Assert.That(JToken.DeepEquals(previous.root["settings"], saved.root["settings"]), Is.True);
            Assert.That(JToken.DeepEquals(previous.root["actions"], saved.root["actions"]), Is.True);
            Assert.That(JToken.DeepEquals(previous.root["angleData"], saved.root["angleData"]), Is.True);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
