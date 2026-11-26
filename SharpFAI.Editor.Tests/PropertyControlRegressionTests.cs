using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Util;

namespace SharpFAI.Editor.Tests;

public sealed class PropertyControlRegressionTests
{
    [TestCase("1+2*3", 7)]
    [TestCase("(1+2)*3-8/4", 7)]
    [TestCase("-.5 * -2e1 + 9 ÷ 3 × 2", 16)]
    [TestCase("8/4/2", 1)]
    public void NumericFieldsEvaluateMixedExpressions(string text, double expected)
    {
        Assert.That(EditorPropertyEdit.TryNumber(new("value", "Float", null), text, out var result), Is.True);
        Assert.That(result.Value<double>(), Is.EqualTo(expected));
    }

    [TestCase("1/(2-2)")]
    [TestCase("(2+3")]
    [TestCase("1e308*10")]
    [TestCase("2+3garbage")]
    [TestCase("5/2")]
    [TestCase("60+60")]
    public void IntegerExpressionsStillRequireFiniteWholeValuesInRange(string text)
    {
        Assert.That(EditorPropertyEdit.TryNumber(new("count", "Int", null, Minimum: 0, Maximum: 100), text, out _), Is.False);
    }

    [TestCase("ScreenTile", "0.95", "-1.75", "20.01")]
    [TestCase("AddDecoration", "1.25", "2.5", "0.95")]
    public void TilingFieldsKeepFractionalComponents(string type, string x, string y, string outsideRange)
    {
        var catalog = new EditorCatalog(null);
        EditorField[] fields = catalog.Find(type)!.Fields;
        EditorField tile = fields.Single(field => field.Key == "tile");
        var item = catalog.Create(type, 1);
        Assert.That(tile.Type, Is.EqualTo("Vector2"));
        Assert.That(item["tile"]!.Values<double>(), Is.EqualTo(new[] { 1d, 1d }));
        Assert.That(EditorPropertyEdit.TryNumber(tile, outsideRange, out _), Is.False);
        Assert.That(EditorPropertyEdit.TryNumber(tile, x, out var first), Is.True);
        Assert.That(EditorPropertyEdit.TryNumber(tile, y, out var second), Is.True);
        item["tile"] = EditorPropertyEdit.WithComponent(item["tile"], tile, 0, first);
        item["tile"] = EditorPropertyEdit.WithComponent(item["tile"], tile, 1, second);
        Assert.That(JObject.Parse(item.ToString())["tile"]!.Values<double>(),
            Is.EqualTo(new[] { first.Value<double>(), second.Value<double>() }));

        if (type != "AddDecoration") return;
        Assert.That(InspectorFields.GetState(item, tile, fields, type).Enabled, Is.True);
        item["maskingType"] = "Mask";
        Assert.That(InspectorFields.GetState(item, tile, fields, type).Enabled, Is.False);
        item["maskingType"] = "None";
        item["blendMode"] = "Multiply";
        Assert.That(InspectorFields.GetState(item, tile, fields, type).Enabled, Is.False);
    }

    [TestCase("bpm", 0.001)]
    [TestCase("volume", 1)]
    [TestCase("hitsoundVolume", 1)]
    [TestCase("pitch", 1)]
    public void SongNumberEditorsRejectZeroAndClampDragging(string property, double minimum)
    {
        EditorField field = new EditorCatalog(null).SettingsFor("SongSettings").Single(item => item.Key == property);
        Assert.That(EditorPropertyEdit.TryNumber(field, "0", out _), Is.False);
        Assert.That(EditorPropertyEdit.ClampNumericDrag(field, 0), Is.EqualTo(minimum));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TempoFieldsAcceptValuesAboveTheOldLimits(bool importedSchema)
    {
        var catalog = importedSchema ? EditorCatalog.FromSchema(JObject.Parse("""
            {"levelEvents":[{"name":"SetSpeed","properties":[
                {"name":"beatsPerMinute","type":"Float","max":10000},
                {"name":"bpmMultiplier","type":"Float","max":128}]}],
             "categories":[{"name":"Gameplay","events":["SetSpeed"]}],
             "settings":[{"name":"SongSettings","properties":[
                 {"name":"bpm","type":"Float","max":10000}]}]}
            """)) : new EditorCatalog(null);
        var baseBpm = catalog.SettingsFor("SongSettings").Single(field => field.Key == "bpm");
        var speed = catalog.Find("SetSpeed")!.Fields;
        var absolute = speed.Single(field => field.Key == "beatsPerMinute");
        var multiplier = speed.Single(field => field.Key == "bpmMultiplier");
        foreach (var field in new[] { baseBpm, absolute, multiplier })
        {
            Assert.That(field.Maximum, Is.Null, field.Key);
            Assert.That(EditorPropertyEdit.ClampNumericDrag(field, 1e8), Is.EqualTo(1e8), field.Key);
            Assert.That(EditorPropertyEdit.TryNumber(field, "0", out _), Is.False, field.Key);
            Assert.That(EditorPropertyEdit.TryNumber(field, "1/0", out _), Is.False, field.Key);
        }
        Assert.That(EditorPropertyEdit.TryNumber(baseBpm, "25000", out var chartBpm), Is.True);
        Assert.That(EditorPropertyEdit.TryNumber(multiplier, "128*2", out var factor), Is.True);
        Assert.That(EditorPropertyEdit.TryNumber(absolute, "100000", out var eventBpm), Is.True);

        var chart = new EditorDocument();
        chart.InsertTiles(0, [0, 0, 0]);
        chart.Set(chart.Settings, "bpm", chartBpm);
        chart.Add(new() { ["floor"] = 1, ["eventType"] = "SetSpeed", ["speedType"] = "Multiplier", ["bpmMultiplier"] = factor }, false);
        chart.Add(new() { ["floor"] = 2, ["eventType"] = "SetSpeed", ["speedType"] = "Bpm", ["beatsPerMinute"] = eventBpm }, false);
        var level = chart.SnapshotForPlayback();
        var core = FloorCoreEvents.Read(level);
        var timeline = LevelRenderer.BuildTimeline(level, level.CreatePlaybackFloors(core), PlaybackEventBinder.BindForEditor(level), core);
        Assert.That(timeline.FloorBpms.Take(3), Is.EqualTo(new[] { 25_000d, 6_400_000d, 100_000d }));
    }
    [Test]
    public void OtherFieldsExcludeMetadataAndOnlyListUnrecognizedProperties()
    {
        var target = JObject.Parse("""
            {"floor":2,"eventType":"MoveDecorations","active":false,"locked":true,"visible":true,"disabled":{},"opacity":50}
            """);
        EditorField[] fields = [new("opacity", "Float", null)];
        Assert.That(InspectorFields.OtherFields(target, fields, "MoveDecorations"), Is.Empty);

        target["futureProperty"] = 42;
        Assert.That(InspectorFields.OtherFields(target, fields, "MoveDecorations").Select(field => field.Key),
            Is.EqualTo(new[] { "futureProperty" }));
    }

    [Test]
    public void AdvancedFilterFieldsAreNotRepeatedOutsideTheFilterEditor()
    {
        var target = JObject.Parse("""
            {"eventType":"SetFilterAdvanced","filter":"CameraFilterPack_Colors_Brightness","filterProperties":{"futureParameter":42}}
            """);
        EditorField parameter = EditorFilterCatalog.Fields(target.Value<string>("filter")!).First();
        target[parameter.Key] = parameter.Default!.DeepClone();
        EditorField[] fields = [new("filter", "AdvancedFilter", null)];
        Assert.That(InspectorFields.OtherFields(target, fields, "SetFilterAdvanced"), Is.Empty);
    }

    [Test]
    public void ContinuousPreviewChangesUndoAndRedoAsOneEdit()
    {
        var doc = new EditorDocument();
        var item = new JObject { ["floor"] = 0, ["eventType"] = "MoveDecorations", ["opacity"] = 100 };
        doc.Add(item, false);
        int initialVersion = doc.ChangeVersion;
        object gesture = new();
        for (int opacity = 99; opacity >= 50; opacity--)
            doc.Set(item, "opacity", new JValue(opacity), gesture);
        Assert.That(doc.ChangeVersion, Is.EqualTo(initialVersion + 50));
        Assert.That(doc.EffectChangesSince(initialVersion), Has.Length.EqualTo(1));
        Assert.That(doc.EffectChangesSince(initialVersion)[0].Value.Value<int>("opacity"), Is.EqualTo(50));
        doc.Undo();
        Assert.That(item.Value<int>("opacity"), Is.EqualTo(100));
        doc.Redo();
        Assert.That(item.Value<int>("opacity"), Is.EqualTo(50));
        int version = doc.ChangeVersion;
        doc.Set(item, "opacity", new JValue(50), gesture);
        Assert.That(doc.ChangeVersion, Is.EqualTo(version));
    }

    [Test]
    public void EscapeRollsBackAnEntirePreviewGesture()
    {
        var doc = new EditorDocument();
        var item = new JObject { ["floor"] = 0, ["eventType"] = "MoveCamera", ["zoom"] = 100 };
        doc.Add(item, false);
        object gesture = new();
        doc.Set(item, "zoom", new JValue(110), gesture);
        doc.Set(item, "zoom", new JValue(120), gesture);
        doc.CancelEditGroup(gesture);
        Assert.That(item.Value<int>("zoom"), Is.EqualTo(100));
        Assert.That(doc.CanRedo, Is.False);
    }

    [Test]
    public void NestedGradientValuesCommitThroughTheOwningParticle()
    {
        var doc = new EditorDocument();
        var particle = JObject.Parse("""
            {"floor":0,"eventType":"AddParticle","colorOverLifetime":{"mode":"Gradient",
             "gradient1":{"colorKeys":[{"time":0,"color":"ffffff"},{"time":1,"color":"000000"}]}}}
            """);
        doc.Add(particle, true);
        var point = (JObject)particle["colorOverLifetime"]!["gradient1"]!["colorKeys"]![0]!;
        var binding = EditorPropertyEdit.Capture(doc, point, "time");
        Assert.That(binding.Owner, Is.SameAs(particle));
        binding.Set(new JValue(.25));
        Assert.That(binding.Read()!.Value<double>(), Is.EqualTo(.25));
        doc.Undo();
        Assert.That(binding.Read()!.Value<double>(), Is.Zero);
        doc.Redo();
        Assert.That(binding.Read()!.Value<double>(), Is.EqualTo(.25));
    }

    [Test]
    public void NestedSliderBindingSurvivesRootCopiesAndKeepsOneUndoGroup()
    {
        var doc = new EditorDocument();
        var item = JObject.Parse("""{"floor":0,"eventType":"SetFilterAdvanced","filterProperties":{"filter_Size":1}}""");
        doc.Add(item, false);
        var first = EditorPropertyEdit.Capture(doc, (JObject)item["filterProperties"]!, "filter_Size");
        object gesture = new();
        first.Set(new JValue(2), gesture);
        var next = EditorPropertyEdit.Capture(doc, (JObject)item["filterProperties"]!, "filter_Size");
        Assert.That(first.Matches(next), Is.True);
        next.Set(new JValue(3), gesture);
        doc.Undo();
        Assert.That(first.Read()!.Value<int>(), Is.EqualTo(1));
        doc.Redo();
        Assert.That(next.Read()!.Value<int>(), Is.EqualTo(3));
    }

    [Test]
    public void ChangingOneVectorOrRangeComponentPreservesOtherAndNullComponents()
    {
        var vector = new EditorField("positionOffset", "Vector2", null);
        Assert.That(EditorPropertyEdit.WithComponent(new JArray(10, JValue.CreateNull()), vector, 0, new JValue(20)).ToString(),
            Is.EqualTo(new JArray(20, JValue.CreateNull()).ToString()));
        var range = new EditorField("velocity", "Vector2Range", null);
        Assert.That(EditorPropertyEdit.WithComponent(new JArray(new JArray(1, 2), new JArray(3, 4)), range, 2, new JValue(9)).ToString(),
            Is.EqualTo(new JArray(new JArray(1, 2), new JArray(9, 4)).ToString()));
        var tile = new EditorField("startTile", "Tile", null);
        Assert.That(EditorPropertyEdit.WithComponent(new JArray(2, "End"), tile, 0, new JValue(-3))[1]!.Value<string>(), Is.EqualTo("End"));
    }

    [TestCase("NaN")]
    [TestCase("Infinity")]
    [TestCase("-")]
    [TestCase("1.5")]
    [TestCase("101")]
    public void InvalidOrOutOfRangeIntegerInputIsRejected(string text)
    {
        Assert.That(EditorPropertyEdit.TryNumber(new("count", "Int", null, Minimum: 0, Maximum: 100), text, out _), Is.False);
    }

    [Test]
    public void SaveAndReopenKeepTheCommittedValueAndUnknownParameters()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sharpfai-properties-{Guid.NewGuid():N}.adofai");
        try
        {
            var doc = new EditorDocument();
            var item = JObject.Parse("""{"floor":0,"eventType":"SetFilterAdvanced","filter":"future_filter","filterProperties":{"filter_Unknown":42}}""");
            doc.Add(item, false);
            var binding = EditorPropertyEdit.Capture(doc, (JObject)item["filterProperties"]!, "filter_Unknown");
            binding.Set(new JValue(75));
            doc.Save(path);
            var reopened = new EditorDocument(path);
            Assert.That(reopened.Events(0).Single().SelectToken("filterProperties.filter_Unknown")!.Value<int>(), Is.EqualTo(75));
            Assert.That(reopened.Modified, Is.False);
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Test]
    public void LoadLegacyFilterParametersAsEditableFieldsWithoutChangingTheSource()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sharpfai-legacy-filter-{Guid.NewGuid():N}.adofai");
        try
        {
            var doc = new EditorDocument();
            doc.Add(JObject.Parse("""{"floor":0,"eventType":"SetFilterAdvanced","filterProperties":"\"filter_Brightness\": 119, \"filter_Unknown\": 42"}"""), false);
            doc.Save(path);
            string original = System.IO.File.ReadAllText(path);

            var reopened = new EditorDocument(path);
            var properties = (JObject)reopened.Events(0).Single()["filterProperties"]!;
            Assert.That(properties.Value<int>("filter_Brightness"), Is.EqualTo(119));
            Assert.That(reopened.Modified, Is.False);
            Assert.That(System.IO.File.ReadAllText(path), Is.EqualTo(original));
            EditorPropertyEdit.Capture(reopened, properties, "filter_Brightness").Set(new JValue(120));
            reopened.Save(path);

            var saved = (JObject)new EditorDocument(path).Events(0).Single()["filterProperties"]!;
            Assert.That(saved.Value<int>("filter_Brightness"), Is.EqualTo(120));
            Assert.That(saved.Value<int>("filter_Unknown"), Is.EqualTo(42));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Test]
    public void ContinuousEditsDoNotMergeAcrossASaveBoundary()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sharpfai-gesture-{Guid.NewGuid():N}.adofai");
        try
        {
            var doc = new EditorDocument();
            var item = new JObject { ["floor"] = 0, ["eventType"] = "MoveCamera", ["zoom"] = 100 };
            doc.Add(item, false);
            object gesture = new();
            doc.Set(item, "zoom", new JValue(110), gesture);
            doc.Save(path);
            doc.Set(item, "zoom", new JValue(120), gesture);
            doc.Undo();
            Assert.That(item.Value<int>("zoom"), Is.EqualTo(110));
            Assert.That(doc.Modified, Is.False);
            doc.Undo();
            Assert.That(item.Value<int>("zoom"), Is.EqualTo(100));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    [Test]
    public void AdvancedFilterDefaultsUseCameraScopeAndKeepIntegerUnits()
    {
        var camera = EditorFilterCatalog.Fields("CameraFilterPackLegacy_Edge_Neon");
        if (camera.Length == 0) Assert.Ignore("Generated game filters unavailable.");
        Assert.That(camera.Single(field => field.Key == "filter_EdgeWeight").Default!.Value<float>(), Is.EqualTo(750f));
        Assert.That(EditorFilterCatalog.Fields("CameraFilterPackLegacy_Edge_Neon", camera: false)
            .Single(field => field.Key == "filter_EdgeWeight").Default!.Value<float>(), Is.EqualTo(100f));
        var led = EditorFilterCatalog.Fields("CameraFilterPackLegacy_TV_LED").Single(field => field.Key == "filter_Size");
        Assert.That(led.Type, Is.EqualTo("Int"));
        Assert.That(led.Default!.Value<int>(), Is.EqualTo(5));
    }
}
