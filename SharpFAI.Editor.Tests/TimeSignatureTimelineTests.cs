using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor.Tests;

public sealed class TimeSignatureTimelineTests
{
    [TestCase(0, "00:00.000")]
    [TestCase(.001, "00:00.001")]
    [TestCase(59.9996, "01:00.000")]
    [TestCase(6000.123, "100:00.123")]
    [TestCase(-.001, "-00:00.001")]
    public void TimelinePositionAlwaysShowsMilliseconds(double seconds, string label) =>
        Assert.That(Timeline.FormatPosition(seconds), Is.EqualTo(label));

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

    [TestCase(1.0, 0.0)]
    [TestCase(0.5, -0.05)]
    [TestCase(2.0, 0.05)]
    public void MetronomeUsesLevelBpmAndMeterWithTheSameSamplesAfterASeek(double rate, double shift)
    {
        const int sampleRate = 48000;
        var clock = new EditorBeatClock([-2, .2, 1.2], 120);
        var meters = EditorTimeSignatures.Read(null).WithChange(2, new TimeSignature(3, 8), 240);
        var metronome = new EditorMetronome(clock, meters);
        double step = rate / sampleRate;
        float[] whole = new float[(int)(2.3 / step)];
        float[] block = new float[960];
        metronome.Render(0, step, shift, whole);

        int first = FrameAt(.2);
        int regular = FrameAt(.7);
        int meterChange = FrameAt(1.2);
        int eighthNote = FrameAt(1.45);
        int nextBar = FrameAt(1.95);
        Assert.Multiple(() =>
        {
            Assert.That(whole.Take(first), Is.All.Zero);
            Assert.That(whole[first..(first + 180)], Is.EqualTo(whole[meterChange..(meterChange + 180)]));
            Assert.That(whole[regular..(regular + 180)], Is.EqualTo(whole[eighthNote..(eighthNote + 180)]));
            Assert.That(whole[first..(first + 180)], Is.EqualTo(whole[nextBar..(nextBar + 180)]));
            Assert.That(whole[first..(first + 180)].Max(), Is.GreaterThan(whole[regular..(regular + 180)].Max()));
            Assert.That(whole[(eighthNote + 1440)..FrameAt(1.7)], Is.All.Zero);
            metronome.Render(eighthNote - 200, step, shift, block);
            Assert.That(block, Is.EqualTo(whole[(eighthNote - 200)..(eighthNote + 760)]));
            metronome.Render(eighthNote + 80, step, shift, block);
            Assert.That(block, Is.EqualTo(whole[(eighthNote + 80)..(eighthNote + 1040)]));
        });

        int FrameAt(double time) => (int)Math.Round((time - shift) / step);
    }

    [Test]
    public void TimelineTempoStartsFromLevelBpmAndOnlyChangesWithinAnOverriddenSection()
    {
        double[] floors = [-2, .2, 1.2];
        var meters = EditorTimeSignatures.Read(null)
            .WithChange(2, new TimeSignature(6, 8), 180)
            .WithChange(5, new TimeSignature(3, 4));
        var timeline = new EditorBeatClock(floors, 120, meters);
        var metronome = new EditorBeatClock(floors, 120);
        var initialOverride = new EditorBeatClock(floors, 120, meters.WithChange(0, TimeSignature.Common, 240));
        var floorClock = new EditorBeatClock(floors, [120, 120, 240], 120);

        Assert.Multiple(() =>
        {
            Assert.That(timeline.TimeAt(2), Is.EqualTo(1.2).Within(1e-9));
            Assert.That(timeline.TimeAt(5), Is.EqualTo(2.2).Within(1e-9));
            Assert.That(timeline.TimeAt(6), Is.EqualTo(2.7).Within(1e-9));
            Assert.That(timeline.BeatAt(2.2), Is.EqualTo(5).Within(1e-9));
            Assert.That(metronome.TimeAt(5), Is.EqualTo(2.7).Within(1e-9));
            Assert.That(floorClock.TimeAt(5), Is.EqualTo(1.95).Within(1e-9));
            Assert.That(initialOverride.TimeAt(-1), Is.EqualTo(-.3).Within(1e-9));
            Assert.That(initialOverride.TimeAt(1), Is.EqualTo(.45).Within(1e-9));
        });
    }

    [Test]
    public void InvalidOrMissingTempoOverridesLeaveLegacyMeterChangesUsable()
    {
        var meters = EditorTimeSignatures.Read(JArray.Parse("""
            [
              { "beat": 2, "numerator": 3, "denominator": 4 },
              { "beat": 5, "numerator": 7, "denominator": 8, "bpm": -120 }
            ]
            """));
        Assert.Multiple(() =>
        {
            Assert.That(meters.Changes[1].Signature, Is.EqualTo(new TimeSignature(3, 4)));
            Assert.That(meters.Changes[2].Signature, Is.EqualTo(new TimeSignature(7, 8)));
            Assert.That(meters.Changes.Select(change => change.TimelineBpm), Is.All.Null);
            Assert.That(meters.WithChange(5, new TimeSignature(6, 8), double.NaN), Is.SameAs(meters));
            Assert.That(meters.Write()!.OfType<JObject>().All(change => change["bpm"] is null), Is.True);
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
            document.SetTimeSignature(0, TimeSignature.Common, 150.25);
            document.SetTimeSignature(9, new TimeSignature(7, 8), 240);
            Assert.That(document.Modified, Is.True);
            Assert.That(document.PlaybackVersion, Is.Zero);
            document.Undo();
            Assert.That(document.TimeSignatures.Changes, Has.Count.EqualTo(1));
            Assert.That(document.TimeSignatures.Changes[0].TimelineBpm, Is.EqualTo(150.25));
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
