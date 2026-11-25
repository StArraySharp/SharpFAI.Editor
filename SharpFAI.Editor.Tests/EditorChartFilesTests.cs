using NUnit.Framework;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Util;

namespace SharpFAI.Editor.Tests;

[TestFixture]
public sealed class EditorChartFilesTests
{
    private DirectoryInfo _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Directory.CreateTempSubdirectory("sharpfai-chart-files-");

    [TearDown]
    public void TearDown() => _directory.Delete(recursive: true);

    [Test]
    public void AMinuteCreatesARollingSnapshotWithoutSavingTheOriginalChart()
    {
        string source = Path.Combine(_directory.FullName, "谱面.adofai");
        var chart = new EditorDocument();
        chart.Save(source);
        string original = File.ReadAllText(source);
        var clock = new TestClock();
        var backup = new EditorAutoBackup(Path.Combine(_directory.FullName, "fallback"), clock);
        backup.Update(chart);
        chart.Set(chart.Settings, "song", "First edit");

        clock.Seconds = 59;
        backup.Update(chart);
        Assert.That(_directory.GetFiles("*.sharpfai-backup-*.adofai"), Is.Empty);

        clock.Seconds = 60;
        backup.Update(chart);
        chart.Set(chart.Settings, "song", "Second edit");
        backup.CompleteWrite(wait: true);
        var copy = _directory.GetFiles("*.sharpfai-backup-*.adofai").Single();
        Assert.That(new EditorDocument(copy.FullName).Settings.Value<string>("song"), Is.EqualTo("First edit"));

        clock.Seconds = 120;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        Assert.That(_directory.GetFiles("*.sharpfai-backup-*.adofai").Single().FullName, Is.EqualTo(copy.FullName));
        Assert.That(new EditorDocument(copy.FullName).Settings.Value<string>("song"), Is.EqualTo("Second edit"));
        Assert.That(File.ReadAllText(source), Is.EqualTo(original));
        Assert.That(chart.Path, Is.EqualTo(source));
        Assert.That(chart.Modified, Is.True);
    }

    [Test]
    public void UntitledChartsAreBackedUpInApplicationStorageOnlyAfterEditing()
    {
        var chart = new EditorDocument();
        var clock = new TestClock();
        string destination = Path.Combine(_directory.FullName, "Backups");
        var backup = new EditorAutoBackup(destination, clock);
        backup.Update(chart);
        clock.Seconds = 60;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        Assert.That(Directory.Exists(destination), Is.False);

        chart.Set(chart.Settings, "song", "An unsaved chart");
        clock.Seconds = 120;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        var reopened = new EditorDocument(Directory.GetFiles(destination, "*.adofai").Single());
        Assert.That(reopened.Settings.Value<string>("song"), Is.EqualTo("An unsaved chart"));
        Assert.That(chart.Path, Is.Null);
        Assert.That(chart.Modified, Is.True);
    }

    [Test]
    public void SavedAndRestoredValuesDoNotProduceBackups()
    {
        string source = Path.Combine(_directory.FullName, "saved.adofai");
        var chart = new EditorDocument();
        chart.Save(source);
        var clock = new TestClock();
        var backup = new EditorAutoBackup(Path.Combine(_directory.FullName, "fallback"), clock);
        backup.Update(chart);
        clock.Seconds = 60;
        backup.Update(chart);
        var original = chart.Settings["song"]?.DeepClone();
        chart.Set(chart.Settings, "song", "Temporary");
        chart.Set(chart.Settings, "song", original);
        clock.Seconds = 120;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        Assert.That(_directory.GetFiles("*.sharpfai-backup-*.adofai"), Is.Empty);
    }

    [Test]
    public void SaveAsCopiesReferencedMusicAndDecorationImages()
    {
        string source = _directory.CreateSubdirectory("original").FullName;
        string destination = _directory.CreateSubdirectory("copied").FullName;
        var chart = new EditorDocument();
        chart.Set(chart.Settings, "songFilename", "audio/song.mp3");
        chart.Add(JObject.Parse("""{"floor":0,"eventType":"AddDecoration","decorationImage":"images/装飾.png"}"""), true);
        chart.Save(Path.Combine(source, "level.adofai"));
        Directory.CreateDirectory(Path.Combine(source, "audio"));
        File.WriteAllText(Path.Combine(source, "audio", "song.ogg"), "music fixture");
        Directory.CreateDirectory(Path.Combine(source, "images"));
        File.WriteAllText(Path.Combine(source, "images", "装飾.png"), "image fixture");

        string result = Path.Combine(destination, "level.adofai");
        chart.Save(result);
        Assert.Multiple(() =>
        {
            Assert.That(LevelAudioLocator.Find(chart.SnapshotForPlayback(), result), Is.EqualTo(Path.Combine(destination, "audio", "song.ogg")));
            Assert.That(File.ReadAllText(Path.Combine(destination, "images", "装飾.png")), Is.EqualTo("image fixture"));
            Assert.That(new EditorDocument(result).Decorations[0]!.Value<string>("decorationImage"), Is.EqualTo("images/装飾.png"));
        });
    }

    [Test]
    public void AutomaticBackupContainsAllJsonChunks()
    {
        string source = Path.Combine(_directory.FullName, "large.adofai");
        var chart = new EditorDocument();
        for (int i = 0; i < 600; i++)
            chart.Add(new JObject { ["floor"] = 0, ["eventType"] = "EditorComment", ["comment"] = new string('文', 128) }, false);
        chart.Save(source);

        var clock = new TestClock();
        var backup = new EditorAutoBackup(_directory.FullName, clock);
        backup.Update(chart);
        chart.Set(chart.Settings, "song", "Snapshot after save");
        clock.Seconds = 60;
        backup.Update(chart);
        backup.CompleteWrite(wait: true);
        var reopened = new EditorDocument(_directory.GetFiles("*.sharpfai-backup-*.adofai").Single().FullName);
        Assert.That(JToken.DeepEquals(reopened.SnapshotForPlayback().root, chart.SnapshotForPlayback().root), Is.True);
    }

    [Test]
    public void MidspinCannotStartTheChartAndDeletingAllTilesCanBeUndone()
    {
        var chart = new EditorDocument();
        int initial = chart.LastFloor;
        chart.InsertTiles(0, [999]);
        Assert.That(chart.LastFloor, Is.EqualTo(initial));
        Assert.That(chart.CanUndo, Is.False);
        chart.DeleteTiles(1, initial);
        Assert.That(chart.LastFloor, Is.Zero);
        chart.Undo();
        Assert.That(chart.LastFloor, Is.EqualTo(initial));
    }

    [Test]
    public void PendingInsertAndAppendCanRebuildPlaybackWithoutSnapshottingTheEditor()
    {
        var chart = new EditorDocument();
        var later = JObject.Parse("""{"floor":4,"eventType":"MoveTrack","startTile":[4,"Start"],"endTile":[-1,"End"]}""");
        chart.Add(later, decoration: false);
        int version = chart.PlaybackVersion;
        var playback = chart.SnapshotForPlayback();
        chart.ForgetEffectChangesThrough(version);

        chart.InsertTiles(2, [90]);
        var added = JObject.Parse("""{"floor":3,"eventType":"Twirl"}""");
        chart.Add(added, decoration: false);
        chart.PlaybackPatchSince(version)!.Apply(playback.root);
        Assert.That(JToken.DeepEquals(playback.root, chart.SnapshotForPlayback().root), Is.True);
        Assert.That(chart.Events(5), Is.EqualTo(new[] { later }));
        Assert.That(chart.Events(3), Is.EqualTo(new[] { added }));
        chart.Undo();
        chart.Undo();
        Assert.That(chart.Events(4), Is.EqualTo(new[] { later }));
        Assert.That(chart.Events(5), Is.Empty);
        Assert.That(chart.PlaybackPatchSince(version), Is.Null);
    }

    [Test]
    public void LargeChartTailAppendPreservesEarlierBindingsAndUndo()
    {
        var chart = new EditorDocument();
        chart.InsertTiles(0, new double[20_000]);
        int oldLast = chart.LastFloor;
        var first = JObject.Parse("""{"floor":1,"eventType":"MoveTrack","startTile":[1,"Start"],"endTile":[2,"Start"]}""");
        var tail = JObject.Parse("""{"floor":3,"eventType":"Twirl"}""");
        tail["floor"] = oldLast;
        chart.Add(first, false);
        chart.Add(tail, false);
        var original = chart.SnapshotForPlayback();
        int version = chart.PlaybackVersion;
        chart.ForgetEffectChangesThrough(version);

        chart.InsertTiles(oldLast, [90]);
        var patch = chart.PlaybackPatchSince(version)!;
        Assert.That(patch.PreservesBindings, Is.True);
        patch.Apply(original.root);
        Assert.That(JToken.DeepEquals(original.root, chart.SnapshotForPlayback().root), Is.True);
        Assert.That(chart.Events(oldLast).Single(), Is.SameAs(tail));
        Assert.That(chart.DecorationListVersion, Is.Zero);
        chart.Undo();
        Assert.That(chart.LastFloor, Is.EqualTo(oldLast));
        chart.Redo();
        chart.InsertTiles(oldLast - 1, [180]);
        Assert.That(chart.Events(oldLast + 1).Single(), Is.SameAs(tail));
        chart.Undo();
        Assert.That(chart.Events(oldLast).Single(), Is.SameAs(tail));
    }

    [Test]
    public void MixedAppendedActionsAndDecorationsPreserveEventOrderAndExistingModels()
    {
        var chart = new EditorDocument();
        chart.Add(JObject.Parse("""{"floor":0,"eventType":"MoveDecorations","tag":"old","opacity":80}"""), false);
        chart.Add(JObject.Parse("""{"floor":0,"eventType":"AddText","decText":"old"}"""), true);
        var original = chart.SnapshotForPlayback();
        var previous = PlaybackEventBinder.BindForEditor(original);
        int version = chart.PlaybackVersion;
        chart.ForgetEffectChangesThrough(version);
        int last = chart.LastFloor;
        chart.Add(new JObject { ["floor"] = last, ["eventType"] = "AddText", ["decText"] = "new" }, true);
        chart.Add(new JObject { ["floor"] = last, ["eventType"] = "MoveCamera", ["duration"] = 2 }, false);
        var patch = chart.PlaybackPatchSince(version)!;
        var updated = PlaybackEventBinder.AppendEditorEvents(previous, patch.AppendedEvents!, chart.Settings);
        var full = PlaybackEventBinder.BindForEditor(chart.SnapshotForPlayback());
        Assert.That(updated.Select(item => (item.EventType, item.Floor, item.SourceIndex)),
            Is.EqualTo(full.Select(item => (item.EventType, item.Floor, item.SourceIndex))));
        Assert.That(updated[0], Is.SameAs(previous[0]));
        Assert.That(updated[2], Is.SameAs(previous[1]));
    }

    [Test]
    public void ConsecutiveTileInsertionsPreserveAnchorsAndMatchAFullPlaybackRebuild()
    {
        var chart = new EditorDocument();
        chart.InsertTiles(0, [0, 90, 90, 0, 315, 0]);
        var motion = JObject.Parse("""{"floor":5,"eventType":"MoveTrack","startTile":[4,"Start"],"endTile":[-1,"End"]}""");
        var decoration = JObject.Parse("""{"floor":4,"eventType":"AddDecoration","decorationImage":"test.png"}""");
        chart.Add(motion, decoration: false);
        chart.Add(decoration, decoration: true);
        chart.Add(JObject.Parse("""{"floor":4,"eventType":"SetSpeed","speedType":"Bpm","beatsPerMinute":210}"""), false);
        chart.Add(JObject.Parse("""{"floor":5,"eventType":"Twirl"}"""), false);
        chart.Add(JObject.Parse("""{"floor":3,"eventType":"Pause","active":false,"duration":2}"""), false);
        chart.Add(JObject.Parse("""{"floor":5,"eventType":"PlanetRadius","scale":80}"""), false);
        chart.Add(JObject.Parse("""{"floor":4,"eventType":"MoveDecorations","tag":"test","rotation":10}"""), false);
        chart.Add(JObject.Parse("""{"floor":5,"eventType":"PositionTrack","relativeTo":[4,"Start"],"positionOffset":[0,0]}"""), false);
        int version = chart.PlaybackVersion;
        var original = chart.SnapshotForPlayback();
        var oldEvents = PlaybackEventBinder.BindForEditor(original);
        chart.ForgetEffectChangesThrough(version);

        chart.InsertTiles(2, [45]);
        chart.InsertTiles(3, [90, 135]);
        chart.InsertTiles(5, [0]);
        Assert.That(chart.HasOnlyTileInsertionsSince(version), Is.True);
        var patched = original.CreateSnapshot(chart.PlaybackPath);
        chart.PlaybackPatchSince(version)!.Apply(patched.root);
        patched = patched.CreateSnapshot(chart.PlaybackPath);
        Assert.That(JToken.DeepEquals(patched.root, chart.SnapshotForPlayback().root), Is.True);
        Assert.That(chart.Events(9), Does.Contain(motion));
        Assert.That(EditorDocument.Floor(decoration), Is.EqualTo(8));

        var full = PlaybackEventBinder.ForPlayback(patched);
        var reduced = PlaybackEventBinder.ForEditorFloorTiming(patched);
        var fullyBound = PlaybackEventBinder.BindForEditor(patched);
        var relocated = PlaybackEventBinder.RelocateEditorEvents(original, patched, oldEvents, CancellationToken.None);
        Assert.That(relocated.Select(e => (e.EventType, e.Floor, e.SourceIndex)),
            Is.EqualTo(fullyBound.Select(e => (e.EventType, e.Floor, e.SourceIndex))));
        Assert.That(JToken.DeepEquals(JArray.FromObject(relocated), JArray.FromObject(fullyBound)), Is.True);
        Assert.That(JToken.DeepEquals(JArray.FromObject(PlaybackEventBinder.BindForPlayback(full)), JArray.FromObject(fullyBound)), Is.True);
        var position = relocated.OfType<PositionTrack>().Single();
        Assert.That(position.RelativeTo!.Value.Resolve(position.Floor, chart.LastFloor), Is.EqualTo(8));
        var fullFloors = full.CreateFloors(default, false, false);
        var reducedFloors = reduced.CreateFloors(default, false, false);
        Assert.That(reducedFloors.Select(f => (f.index, f.angle, f.bpm, f.icon, f.isCW)),
            Is.EqualTo(fullFloors.Select(f => (f.index, f.angle, f.bpm, f.icon, f.isCW))));
        Assert.That(LevelRenderer.BuildTimeline(reduced, reducedFloors, relocated).EntryTimes,
            Is.EqualTo(LevelRenderer.BuildTimeline(full, fullFloors, fullyBound).EntryTimes));

        chart.Undo();
        chart.Undo();
        chart.Undo();
        Assert.That(chart.Events(5), Does.Contain(motion));
        Assert.That(JToken.DeepEquals(chart.SnapshotForPlayback().root, original.root), Is.True);
        chart.Redo();
        chart.Redo();
        chart.Redo();
        Assert.That(JToken.DeepEquals(chart.SnapshotForPlayback().root, patched.root), Is.True);
    }

    [Test]
    public void LastOpenedChartPersistsAcrossSessionsAndCanBeReopened()
    {
        string record = Path.Combine(_directory.FullName, "prefs", "last-chart.txt");
        string chart = Path.Combine(_directory.FullName, "上次的谱面 🌀.adofai");
        var history = new LastOpenedChart(record);
        Assert.That(history.Read(), Is.Null);
        new EditorDocument().Save(chart);

        history.Remember(Path.Combine(_directory.FullName, "previous.adofai"));
        history.Remember(chart);
        string? remembered = new LastOpenedChart(record).Read();
        Assert.That(remembered, Is.EqualTo(chart));
        Assert.That(new EditorDocument(remembered).Path, Is.EqualTo(chart));
    }

    private sealed class TestClock : TimeProvider
    {
        public long Seconds { get; set; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
}
