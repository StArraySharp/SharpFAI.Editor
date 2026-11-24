using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

public sealed class TestEditorEffectUpdates
{
    [Test]
    public void ActionDecorationsAndSeparateDecorationsHaveDifferentPatchSlots()
    {
        var level = Chart();
        level.actions.Insert(0, JObject.Parse("""{"floor":0,"eventType":"Hold","active":false}"""));
        level.actions.Insert(1, JObject.Parse("""{"floor":0,"eventType":"AddDecoration","tag":"action"}"""));
        var playback = PlaybackEventBinder.ForPlayback(level);
        var events = PlaybackEventBinder.BindForEditor(playback, level);
        var slots = LevelRenderer.BuildSlots(events);
        Assert.Multiple(() =>
        {
            Assert.That(events[slots[(1, false)]], Is.TypeOf<AddImageDecoration>());
            Assert.That(((AddImageDecoration)events[slots[(1, false)]]).DecorationIndex, Is.EqualTo(-1));
            Assert.That(((AddImageDecoration)events[slots[(0, true)]]).DecorationIndex, Is.EqualTo(0));
            Assert.That(slots.ContainsKey((0, false)), Is.False);
        });
    }

    [TestCase("opacity", "35")]
    [TestCase("positionOffset", "[12,null]")]
    [TestCase("duration", "1.5")]
    [TestCase("ease", "\"InQuad\"")]
    public void MotionPatchAndUndoMatchFullRebuild(string field, string json)
    {
        var level = Chart();
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var timeline = Build(events);
        var before = events.OfType<MoveImageDecoration>().First();
        var raw = (JObject)level.actions[before.SourceIndex]!;
        var original = (JObject)raw.DeepClone();
        raw[field] = JToken.Parse(json);
        var after = (MoveImageDecoration)PlaybackEventBinder.Rebind(raw, before, null);
        Assert.That(timeline.CanEditMotion(before, after), Is.True);
        timeline.EditMotions(new Dictionary<int, MoveImageDecoration> { [after.SourceIndex] = after });
        AssertEquivalent(timeline, Build(PlaybackEventBinder.BindForEditor(level, level)));
        Assert.That(timeline.ChangedItems, Does.Contain(timeline.Items[0]));
        var undo = (MoveImageDecoration)PlaybackEventBinder.Rebind(original, after, null);
        timeline.EditMotions(new Dictionary<int, MoveImageDecoration> { [undo.SourceIndex] = undo });
        level.actions[before.SourceIndex] = original;
        AssertEquivalent(timeline, Build(PlaybackEventBinder.BindForEditor(level, level)));
    }

    [Test]
    public void EditingFloorObjectPropertiesPreservesScheduledObjectChanges()
    {
        var level = Chart();
        var decorations = (JArray)level.root["decorations"]!;
        decorations.Add(JObject.Parse("""
            {"floor":0,"eventType":"AddObject","objectType":"Floor","tag":"object","trackStyle":"Neon","trackAngle":120}
            """));
        level.actions.Add(JObject.Parse("""
            {"floor":1,"eventType":"SetObject","tag":"object","trackAngle":180,"duration":2,"ease":"Linear"}
            """));
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var timeline = Build(events);
        var obj = events.OfType<AddObjectDecoration>().Single();
        var raw = (JObject)decorations[obj.DecorationIndex]!;
        var old = (JObject)raw.DeepClone();
        raw["trackStyle"] = "Minimal";
        raw["trackAngle"] = 60;
        raw["trackColor"] = "ff00ff";
        var edited = (AddObjectDecoration)PlaybackEventBinder.Rebind(raw, obj, level.root["settings"] as JObject);

        Assert.That(LevelRenderer.CanEditDecorationParameters(old, raw), Is.True);
        timeline.EditInitial(edited, decoration: true);
        var floor = timeline.Items.Single(item => item.IsObject);
        Assert.That(floor.At(0).Tint, Is.EqualTo(new Vector4(1, 0, 1, 1)));
        Assert.That(floor.At(1).ObjectFloor!.Angle, Is.EqualTo(120f));
        Assert.That(floor.At(1).ObjectFloor!.Style, Is.EqualTo(TrackStyleInfo.Minimal));
        AssertEquivalent(timeline, Build(PlaybackEventBinder.BindForEditor(level, level)));
    }

    [Test]
    public void SetObjectFadesRevealedFloorsAndCanBePatchedInPlace()
    {
        var level = Chart();
        var decorations = (JArray)level.root["decorations"]!;
        decorations.Add(JObject.Parse("""{"floor":0,"eventType":"AddObject","objectType":"Floor","tag":"target","trackColor":"ffffff","trackOpacity":0}"""));
        decorations.Add(JObject.Parse("""{"floor":0,"eventType":"AddObject","objectType":"Planet","tag":"target","planetColorType":"Custom","planetColor":"ff0000"}"""));
        level.actions.Clear();
        level.actions.Add(JObject.Parse("""{"floor":1,"eventType":"SetObject","tag":"target","duration":2,"ease":"InQuad","trackOpacity":0,"trackColor":"000000","planetColor":"0000ff"}"""));
        level.actions.Add(JObject.Parse("""{"floor":1,"eventType":"MoveDecorations","tag":"target","opacity":100}"""));
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var timeline = Build(events);
        var floor = timeline.Items[1];
        var planet = timeline.Items[2];
        Assert.That(floor.At(0).Opacity, Is.Zero);
        Assert.That(floor.At(.5).Opacity, Is.EqualTo(1));
        Assert.That(floor.At(1).Opacity, Is.EqualTo(.75f));
        Assert.That(floor.At(1).Tint.X, Is.EqualTo(.75f));
        Assert.That(floor.At(2).Opacity, Is.Zero);
        Assert.That(floor.At(1).Opacity, Is.EqualTo(.75f));
        Assert.That(planet.At(2).Opacity, Is.EqualTo(1));
        Assert.That(planet.At(2).Tint, Is.EqualTo(new Vector4(0, 0, 1, 1)));
        Assert.That(timeline.Items[0].At(2).Opacity, Is.EqualTo(1));
        Assert.That(timeline.Items[0].At(2).Tint, Is.EqualTo(Vector4.One));

        var before = events.OfType<SetObjectDecoration>().Single();
        var raw = (JObject)level.actions[before.SourceIndex]!;
        raw["trackOpacity"] = 40;
        var after = (SetObjectDecoration)PlaybackEventBinder.Rebind(raw, before, null);
        Assert.That(timeline.CanEditMotion(before, after), Is.True);
        timeline.EditMotions(new Dictionary<int, MoveImageDecoration> { [after.SourceIndex] = after });
        Assert.That(floor.At(2).Opacity, Is.EqualTo(.4f));
        Assert.That(timeline.ChangedItems, Does.Not.Contain(timeline.Items[0]));
        AssertEquivalent(timeline, Build(PlaybackEventBinder.BindForEditor(level, level)));
    }

    [Test]
    public void SharedMotionEditsPreserveLastPositionAndLaterOverrides()
    {
        var level = Chart();
        ((JArray)level.root["decorations"]!).Add(level.root["decorations"]![0]!.DeepClone());
        level.actions.Add(JObject.Parse("""{"floor":2,"eventType":"MoveDecorations","tag":"target","relativeTo":"LastPosition","positionOffset":[3,4],"duration":1}"""));
        level.actions.Add(JObject.Parse("""{"floor":3,"eventType":"MoveDecorations","tag":"target","opacity":100,"duration":0}"""));
        level.actions.Add(JObject.Parse("""{"floor":3,"eventType":"MoveDecorations","tag":"target","rotationOffset":120}"""));
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var timeline = Build(events);
        var before = events.OfType<MoveImageDecoration>().First();
        var raw = (JObject)level.actions[before.SourceIndex]!;
        foreach (var (field, value) in new[] { ("opacity", "25"), ("positionOffset", "[20,null]"), ("duration", "0.25") })
        {
            raw[field] = JToken.Parse(value);
            var after = (MoveImageDecoration)PlaybackEventBinder.Rebind(raw, before, null);
            timeline.EditMotions(new Dictionary<int, MoveImageDecoration> { [after.SourceIndex] = after });
            AssertEquivalent(timeline, Build(PlaybackEventBinder.BindForEditor(level, level)));
            before = after;
        }
    }

    [TestCase("opacity", "25")]
    [TestCase("position", "[4,-3]")]
    [TestCase("scale", "[50,125]")]
    [TestCase("relativeTo", "\"Camera\"")]
    [TestCase("depth", "4")]
    [TestCase("visible", "false")]
    public void InitialDecorationPatchReevaluatesItsMotionsWithoutReplacingTheItem(string field, string json)
    {
        var level = Chart();
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var timeline = Build(events);
        var item = timeline.Items[0];
        _ = item.At(.75);
        var before = events.OfType<AddImageDecoration>().Single();
        var raw = (JObject)level.root["decorations"]![0]!;
        var original = (JObject)raw.DeepClone();
        raw[field] = JToken.Parse(json);
        Assert.That(LevelRenderer.CanEditDecorationParameters(original, raw), Is.True);
        var after = (AddImageDecoration)PlaybackEventBinder.Rebind(raw, before, level.root["settings"] as JObject);
        timeline.EditInitial(after, decoration: true);
        Assert.That(timeline.Items[0], Is.SameAs(item));
        Assert.That(timeline.ChangedItems, Does.Contain(item));
        AssertEquivalent(timeline, Build(PlaybackEventBinder.BindForEditor(level, level)));
    }

    [TestCase("tag", "\"different\"")]
    [TestCase("active", "false")]
    [TestCase("decorationImage", "\"other.png\"")]
    [TestCase("decText", "\"changed\"")]
    [TestCase("hitbox", "\"Kill\"")]
    public void StructuralDecorationChangesRequireRebuildingDependencies(string field, string json)
    {
        var before = new JObject { ["eventType"] = "AddDecoration" };
        var after = (JObject)before.DeepClone();
        after[field] = JToken.Parse(json);
        Assert.That(LevelRenderer.CanEditDecorationParameters(before, after), Is.False);
    }

    [TestCase("zoom", "60")]
    [TestCase("position", "[7,null]")]
    [TestCase("duration", "1.25")]
    [TestCase("ease", "\"OutQuad\"")]
    [TestCase("relativeTo", "\"LastPosition\"")]
    public void CameraSuffixEditMatchesRebuildAcrossRepeatedOccurrences(string field, string json)
    {
        var level = Chart();
        level.actions.Clear();
        foreach (string action in new[]
        {
            """{"floor":0,"eventType":"MoveCamera","relativeTo":"Global","position":[1,2],"zoom":90}""",
            """{"floor":1,"eventType":"MoveCamera","position":[3,4],"duration":2,"zoom":120,"eventTag":"repeat"}""",
            """{"floor":1,"eventType":"RepeatEvents","repetitions":2,"interval":0.5,"tag":"repeat"}""",
            """{"floor":2,"eventType":"MoveCamera","position":[9,10],"zoom":100,"duration":0}""",
            """{"floor":3,"eventType":"MoveCamera","rotation":45}""",
        }) level.actions.Add(JObject.Parse(action));
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var floors = Enumerable.Range(0, 4).Select(_ => new Floor(0, 180, default)).ToArray();
        var timing = new LevelRenderer.PlaybackTimeline { EntryTimes = [0, .5, 1, 1.5], FloorBpms = [120, 120, 120, 120] };
        var settings = new LevelPlaybackSettings();
        var before = events.OfType<MoveCameraEvent>().ElementAt(1);
        var original = CameraTimeline.Build(events, floors, timing, settings, 150)!;
        var raw = (JObject)level.actions[before.SourceIndex]!;
        raw[field] = JToken.Parse(json);
        var after = (MoveCameraEvent)PlaybackEventBinder.Rebind(raw, before, null);
        var patched = original.Edit(new Dictionary<int, MoveCameraEvent> { [after.SourceIndex] = after }, floors, timing, settings, 150);
        Assert.That(patched, Is.Not.Null);
        var rebuilt = CameraTimeline.Build(PlaybackEventBinder.BindForEditor(level, level), floors, timing, settings, 150)!;
        foreach (double time in new[] { 0, .5, .6, .75, 1, 1.25, 1.5, 3, .6 })
            Assert.That(patched!.Evaluate(time, new Vector2(20, 30), 100),
                Is.EqualTo(rebuilt.Evaluate(time, new Vector2(20, 30), 100)), $"at {time}");
        raw["angleOffset"] = 90;
        var moved = (MoveCameraEvent)PlaybackEventBinder.Rebind(raw, before, null);
        Assert.That(original.Edit(new Dictionary<int, MoveCameraEvent> { [moved.SourceIndex] = moved }, floors, timing, settings, 150), Is.Null);
    }

    [TestCase("startOpacity", "35")]
    [TestCase("plane", "\"Foreground\"")]
    [TestCase("duration", "2")]
    [TestCase("ease", "\"InQuad\"")]
    public void FlashPatchMatchesRebuildAndKeepsBackgroundAndHistory(string field, string json)
    {
        var level = Chart();
        level.actions.Clear();
        foreach (string action in new[]
        {
            """{"floor":0,"eventType":"CustomBackground","color":"102030"}""",
            """{"floor":1,"eventType":"HallOfMirrors","enabled":true}""",
            """{"floor":1,"eventType":"Flash","startColor":"ff0000","endColor":"0000ff","startOpacity":100,"duration":1,"eventTag":"repeat"}""",
            """{"floor":1,"eventType":"RepeatEvents","repetitions":2,"interval":0.5,"tag":"repeat"}""",
        }) level.actions.Add(JObject.Parse(action));
        var events = PlaybackEventBinder.BindForEditor(level, level);
        var floors = Enumerable.Range(0, 4).Select(_ => new Floor(0, 180, default)).ToArray();
        double[] times = [0, .5, 1, 1.5], bpms = [120, 120, 120, 120];
        var settings = new LevelPlaybackSettings();
        var before = events.OfType<BackdropEvent>().Single(item => item.EventType == EventType.Flash);
        var original = BackdropTimeline.Build(settings, events, floors, times, bpms, null, []);
        var raw = (JObject)level.actions[before.SourceIndex]!;
        raw[field] = JToken.Parse(json);
        var after = (BackdropEvent)PlaybackEventBinder.Rebind(raw, before, null);
        var patched = original.EditFlashes(new Dictionary<int, BackdropEvent> { [after.SourceIndex] = after }, floors, bpms);
        Assert.That(patched, Is.Not.Null);
        var rebuilt = BackdropTimeline.Build(settings, PlaybackEventBinder.BindForEditor(level, level), floors, times, bpms, null, []);
        foreach (double time in new[] { 0, .5, .6, .75, 1, 1.25, 1.5, 3, .6 })
        {
            Assert.That(patched!.BackgroundAt(time), Is.EqualTo(rebuilt.BackgroundAt(time)));
            Assert.That(patched.HistoryAt(time), Is.EqualTo(rebuilt.HistoryAt(time)));
            Assert.That(patched.FlashAt(time, true), Is.EqualTo(rebuilt.FlashAt(time, true)));
            Assert.That(patched.FlashAt(time, false), Is.EqualTo(rebuilt.FlashAt(time, false)));
        }
    }

    private static void AssertEquivalent(ImageDecorationTimeline patched, ImageDecorationTimeline rebuilt)
    {
        foreach (double time in new[] { 0, .5, .75, 1, 1.5, 2, 4, .75 })
            for (int i = 0; i < patched.Items.Count; i++)
                Assert.That(patched.Items[i].At(time), Is.EqualTo(rebuilt.Items[i].At(time)), $"item {i} at {time}");
    }

    private static ImageDecorationTimeline Build(PlaybackEvent[] events)
    {
        var floors = Enumerable.Range(0, 4).Select(_ => new Floor(0, 180, default)).ToArray();
        var images = new Dictionary<string, DecorationImage> { ["base.png"] = new(2, 2, new byte[16]) };
        return ImageDecorationTimeline.Build(events, floors, [0, .5, 1, 1.5], [120, 120, 120, 120],
            null, [], CancellationToken.None, editable: true, cachedImages: images)!;
    }

    private static Level Chart()
    {
        var root = JObject.Parse("""
            {"settings":{"version":15,"bpm":120},"angleData":[0,90,180],
             "decorations":[{"floor":0,"eventType":"AddDecoration","tag":"target","decorationImage":"base.png",
                "position":[1,2],"parallax":[0,0],"pivotOffset":[2,3]}],
             "actions":[
                {"floor":1,"eventType":"MoveDecorations","tag":"target","duration":4,"positionOffset":[10,20],"opacity":80},
                {"floor":2,"eventType":"MoveDecorations","tag":"target","duration":2,"rotationOffset":90}
             ]}
            """);
        return new Level(root.Properties().ToDictionary(property => property.Name, property => (object)property.Value));
    }
}
