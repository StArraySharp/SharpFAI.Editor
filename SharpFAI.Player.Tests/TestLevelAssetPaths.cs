using NUnit.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using SharpFAI.Util;
using System;
using System.IO;

namespace SharpFAI.Player.Tests;

/// <summary>
/// <see cref="LevelAssetPaths"/> checks: an export must never overwrite a file the level itself uses.
/// </summary>
[TestFixture]
public class TestLevelAssetPaths
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"SharpFAIAssets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless
        }
    }

    private Level WriteLevel(string levelName, string settingsJson, params string[] siblings)
    {
        foreach (string sibling in siblings)
        {
            File.WriteAllBytes(Path.Combine(_directory, sibling), []);
        }

        Newtonsoft.Json.Linq.JObject settings = PresetSettings.ApplyDefaults(new Newtonsoft.Json.Linq.JObject());

        foreach (Newtonsoft.Json.Linq.JProperty property in Newtonsoft.Json.Linq.JObject.Parse(settingsJson).Properties())
        {
            settings[property.Name] = property.Value;
        }

        Newtonsoft.Json.Linq.JObject root = new()
        {
            ["angleData"] = new Newtonsoft.Json.Linq.JArray(0, 0, 0),
            ["settings"] = settings,
            ["actions"] = new Newtonsoft.Json.Linq.JArray(),
            ["decorations"] = new Newtonsoft.Json.Linq.JArray(),
        };

        string path = Path.Combine(_directory, levelName);
        File.WriteAllText(path, root.ToString());
        return new Level(path);
    }

    [Test]
    public void VideoExtensionsAreRecognised()
    {
        Assert.That(LevelAssetPaths.IsVideoFile("d4d.mp4"), Is.True);
        Assert.That(LevelAssetPaths.IsVideoFile("clip.MP4"), Is.True);
        Assert.That(LevelAssetPaths.IsVideoFile("song.wav"), Is.False);
        Assert.That(LevelAssetPaths.IsVideoFile(null), Is.False);
        Assert.That(LevelAssetPaths.IsVideoFile(string.Empty), Is.False);
    }

    [Test]
    public void PlainExportUsesTheLevelName()
    {
        Level level = WriteLevel("level.adofai", "{}");

        string output = LevelAssetPaths.ResolveExportPath(level.pathToLevel!, level);

        Assert.That(Path.GetFileName(output), Is.EqualTo("level.mp4"));
    }

    [Test]
    public void ExportDoesNotOverwriteTheBackgroundVideo()
    {
        // level.mp4 is the level's own background video, so the export has to step aside
        Level level = WriteLevel(
            "level.adofai",
            """{ "bgVideo": "level.mp4", "songFilename": "song.wav" }""",
            "level.mp4",
            "song.wav");

        string output = LevelAssetPaths.ResolveExportPath(level.pathToLevel!, level);

        Assert.That(Path.GetFileName(output), Is.Not.EqualTo("level.mp4"));
        Assert.That(Path.GetFileName(output), Is.EqualTo("level-export2.mp4"));
    }

    [Test]
    public void ExportDoesNotOverwriteTheSong()
    {
        Level level = WriteLevel(
            "level.adofai",
            """{ "songFilename": "level.mp4" }""",
            "level.mp4");

        string output = LevelAssetPaths.ResolveExportPath(level.pathToLevel!, level);

        Assert.That(Path.GetFileName(output), Is.EqualTo("level-export2.mp4"));
    }

    [Test]
    public void ExistingUnrelatedExportIsReused()
    {
        // level.mp4 exists but nothing references it: overwriting it is expected behaviour
        Level level = WriteLevel("level.adofai", """{ "songFilename": "song.wav" }""", "level.mp4", "song.wav");

        string output = LevelAssetPaths.ResolveExportPath(level.pathToLevel!, level);

        Assert.That(Path.GetFileName(output), Is.EqualTo("level.mp4"));
    }
}
