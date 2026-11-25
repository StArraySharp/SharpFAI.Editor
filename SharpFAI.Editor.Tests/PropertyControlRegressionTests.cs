using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor.Tests;

public sealed class PropertyControlRegressionTests
{
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
