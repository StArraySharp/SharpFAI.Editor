using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor;

namespace SharpFAI.Editor.Tests;

[TestFixture]
public sealed class EventStackingRegressionTests
{
    private static JObject Event(string type, int floor = 1, int value = 0) =>
        new() { ["eventType"] = type, ["floor"] = floor, ["testValue"] = value };

    [TestCase(false)]
    [TestCase(true)]
    public void GameplayPaletteOffersPlanetEventsAndKeepsExistingAudioInspectable(bool externalSchema)
    {
        EditorCatalog catalog = externalSchema ? EditorCatalog.FromSchema(JObject.Parse("""
            {
                "levelEvents": [
                    {"name":"Hold","properties":[
                        {"name":"duration","type":"Int","default":0,"min":0,"unit":"loops"},
                        {"name":"distanceMultiplier","type":"Int","default":100,"unit":"%"},
                        {"name":"landingAnimation","type":"Bool","default":false}]},
                    {"name":"SetHitsound","properties":[{"name":"hitsound","type":"Enum:HitSound","default":"Kick"}]},
                    {"name":"PlaySound","properties":[{"name":"hitsound","type":"String","default":"Kick"}]},
                    {"name":"AddDecoration","isDecoration":true,"properties":[
                        {"name":"tile","type":"Vector2","min":[1,1],"max":[50,50]}]}
                ],
                "categories": [
                    {"name":"Gameplay","events":["SetHitsound","PlaySound"]},
                    {"name":"Jank","events":["MultiPlanet","Hold","ScaleRadius","PlaySound"]}
                ],
                "settings": []
            }
            """)) : new EditorCatalog(null);

        string[] planetEvents = ["MultiPlanet", "Hold", "ScaleRadius"];
        var gameplay = catalog.Categories.Single(category => category.Key == "Gameplay");
        string[] palette = catalog.Categories.SelectMany(category => category.Events).ToArray();
        var planets = catalog.Find("MultiPlanet")!.Fields.Single(field => field.Key == "planets");
        var hold = catalog.Find("Hold")!.Fields;
        var radius = catalog.Find("ScaleRadius")!.Fields.Single(field => field.Key == "scale");

        Assert.Multiple(() =>
        {
            foreach (string type in planetEvents)
            {
                Assert.That(gameplay.Events, Does.Contain(type));
                Assert.That(palette.Count(name => name == type), Is.EqualTo(1));
            }
            foreach (string type in new[] { "SetHitsound", "PlaySound" })
            {
                Assert.That(palette, Does.Not.Contain(type));
                Assert.That(catalog.Find(type)!.Fields.Select(field => field.Key), Does.Contain("hitsound"));
            }
            Assert.That(EditorCatalog.Options(planets), Is.EqualTo(new[] { "TwoPlanets", "ThreePlanets" }));
            Assert.That(catalog.Create("MultiPlanet", 1).Value<string>("planets"), Is.EqualTo("TwoPlanets"));
            Assert.That(hold.Single(field => field.Key == "duration").Unit, Is.EqualTo("loops"));
            Assert.That(hold.Single(field => field.Key == "duration").Minimum, Is.Zero);
            Assert.That(hold.Single(field => field.Key == "distanceMultiplier").Unit, Is.EqualTo("%"));
            Assert.That(catalog.Create("Hold", 1).Value<int>("distanceMultiplier"), Is.EqualTo(100));
            Assert.That(catalog.Create("Hold", 1).Value<bool>("landingAnimation"), Is.False);
            Assert.That(radius.Minimum, Is.EqualTo(-10000d));
            Assert.That(radius.Maximum, Is.EqualTo(10000d));
            Assert.That(radius.Unit, Is.EqualTo("%"));
            Assert.That(catalog.Create("ScaleRadius", 1).Value<double>("scale"), Is.EqualTo(100d));
            if (externalSchema)
            {
                var bounds = catalog.Find("AddDecoration")!.Fields.Single();
                Assert.That(bounds.Minimum, Is.EqualTo(1d));
                Assert.That(bounds.Maximum, Is.EqualTo(50d));
            }
        });
    }

    [Test]
    public void AddRejectsSoloDuplicatesOnlyOnTheSameFloor()
    {
        EditorDocument document = new();
        JObject original = Event("SetHitsound", value: 1);
        Assert.That(document.Add(original, false), Is.True);
        int version = document.ChangeVersion;

        Assert.That(document.CanAddEvent(1, "SetHitsound"), Is.False);
        Assert.That(document.Add(Event("SetHitsound", value: 2), false), Is.False);
        Assert.That(document.Events(1).Single(), Is.SameAs(original));
        Assert.That(document.ChangeVersion, Is.EqualTo(version));

        Assert.That(document.Add(Event("SetHitsound", floor: 2), false), Is.True);
        Assert.That(document.Add(Event("SetSpeed"), false), Is.True);
        Assert.That(document.Add(Event("SetSpeed"), false), Is.True);
        Assert.That(document.ActionsOfType("SetSpeed").Count(), Is.EqualTo(2));
    }

    [TestCase("Hold", "Pause")]
    [TestCase("Pause", "Hold")]
    [TestCase("Twirl", "FreeRoam")]
    [TestCase("FreeRoam", "Twirl")]
    public void MutuallyExclusiveEventsAreRejectedOnBothAddAndPaste(string existing, string incoming)
    {
        EditorDocument document = new();
        JObject original = Event(existing);
        Assert.That(document.Add(original, false), Is.True);
        int version = document.ChangeVersion;

        Assert.That(document.Add(Event(incoming), false), Is.False);
        document.PasteEvents(1, [Event(incoming, floor: 2)], false);
        Assert.That(document.Events(1).Single(), Is.SameAs(original));
        Assert.That(document.ChangeVersion, Is.EqualTo(version));
    }

    [Test]
    public void PasteKeepsExistingAndFirstAcceptedEventsWithoutDroppingStackableEvents()
    {
        EditorDocument document = new();
        document.Add(Event("Twirl", value: 1), false);
        document.PasteEvents(1, [Event("Twirl", 2, 2), Event("Hold", 2, 3), Event("Pause", 2, 4),
            Event("Hold", 2, 5), Event("MoveCamera", 2, 6), Event("MoveCamera", 2, 7)], false);

        IEnumerable<int> Values() => document.Events(1).Select(item => item.Value<int>("testValue"));
        Assert.That(Values(), Is.EqualTo(new[] { 1, 3, 6, 7 }));
        document.Undo();
        Assert.That(Values(), Is.EqualTo(new[] { 1 }));
        document.Redo();
        Assert.That(Values(), Is.EqualTo(new[] { 1, 3, 6, 7 }));

        document.PasteEvents(1, [Event("AddDecoration"), Event("AddDecoration")], true);
        Assert.That(document.Decorations.Count, Is.EqualTo(2));
    }

    [Test]
    public void LegacyDuplicatesArePreservedOnLoadAndWholeTileCopy()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, $"stacking-{Guid.NewGuid():N}.adofai");
        try
        {
            File.WriteAllText(path, """
                {"angleData":[0,90],"settings":{"version":15},"decorations":[],"actions":[
                    {"floor":1,"eventType":"Twirl","testValue":1},
                    {"floor":1,"eventType":"Twirl","testValue":2}
                ]}
                """);
            EditorDocument document = new(path);
            document.PasteEvents(1, [Event("Twirl", value: 3)], false);
            Assert.That(document.Modified, Is.False);
            document.PasteTiles(2, document.CopyTiles(1, 1), includeDecorations: true);

            Assert.That(document.Events(1).Select(item => item.Value<int>("testValue")), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(document.Events(3).Select(item => item.Value<int>("testValue")), Is.EqualTo(new[] { 1, 2 }));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
