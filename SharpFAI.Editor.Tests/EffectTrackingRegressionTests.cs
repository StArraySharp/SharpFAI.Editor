using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SharpFAI.Editor;

namespace SharpFAI.Editor.Tests;

[TestFixture]
public sealed class EffectTrackingRegressionTests
{
    private static Array Patches(EditorDocument document, int version) =>
        (Array)typeof(EditorDocument).GetMethod(nameof(EditorDocument.EffectChangesSince))!.Invoke(document, [version])!;

    [Test]
    public void EffectValueEditUndoRedoEachProduceCurrentPatch()
    {
        EditorDocument document = new();
        document.PasteEvents(0, [JObject.Parse("{eventType:'MoveCamera',zoom:100}")], false);
        JObject camera = document.Events(0).Last();
        int baseline = document.ChangeVersion;
        document.Set(camera, "zoom", new JValue(150));
        Assert.That(document.HasOnlyEffectChangesSince(baseline), Is.True);
        Assert.That(Patches(document, baseline).Length, Is.EqualTo(1));
        int changed = document.ChangeVersion;
        document.Undo();
        Assert.That(camera.Value<int>("zoom"), Is.EqualTo(100));
        Assert.That(Patches(document, changed).Length, Is.EqualTo(1));
        int undone = document.ChangeVersion;
        document.Redo();
        Assert.That(camera.Value<int>("zoom"), Is.EqualTo(150));
        Assert.That(Patches(document, undone).Length, Is.EqualTo(1));
        document.ForgetEffectChangesThrough(document.ChangeVersion);
        Assert.That(Patches(document, baseline), Is.Empty);
    }

    [Test]
    public void StructuralEditAndItsUndoRemainFullRebuildBarriers()
    {
        EditorDocument document = new();
        document.PasteEvents(0, [JObject.Parse("{eventType:'MoveCamera',zoom:100}")], false);
        JObject camera = document.Events(0).Last();
        int baseline = document.ChangeVersion;
        document.Set(camera, "zoom", new JValue(125));
        document.InsertTiles(0, [45]);
        Assert.That(document.HasOnlyEffectChangesSince(baseline), Is.False);
        Assert.That(Patches(document, baseline), Is.Empty);
        int changed = document.ChangeVersion;
        document.Undo();
        Assert.That(document.HasOnlyEffectChangesSince(changed), Is.False);
        int undone = document.ChangeVersion;
        document.Redo();
        Assert.That(document.HasOnlyEffectChangesSince(undone), Is.False);
    }
}
