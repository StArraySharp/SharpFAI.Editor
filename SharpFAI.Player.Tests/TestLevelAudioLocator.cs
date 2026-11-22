using NUnit.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using SharpFAI.Util;
using System;
using System.IO;

namespace SharpFAI.Player.Tests;

/// <summary>
/// <see cref="LevelAudioLocator"/> checks: the editor must only ever play the song the level
/// declares, never something it found lying next to it.
/// </summary>
[TestFixture]
public class TestLevelAudioLocator
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"SharpFAIAudio_{Guid.NewGuid():N}");
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

    /// <summary>Write a level file whose settings carry the given songFilename.</summary>
    private Level WriteLevel(string songFilename, params string[] siblingFiles)
    {
        foreach (string sibling in siblingFiles)
        {
            File.WriteAllBytes(Path.Combine(_directory, sibling), []);
        }

        Newtonsoft.Json.Linq.JObject settings = PresetSettings.ApplyDefaults(new Newtonsoft.Json.Linq.JObject());
        settings["songFilename"] = songFilename;

        Newtonsoft.Json.Linq.JObject root = new()
        {
            ["angleData"] = new Newtonsoft.Json.Linq.JArray(0, 0, 0),
            ["settings"] = settings,
            ["actions"] = new Newtonsoft.Json.Linq.JArray(),
            ["decorations"] = new Newtonsoft.Json.Linq.JArray(),
        };

        string path = Path.Combine(_directory, "level.adofai");
        File.WriteAllText(path, root.ToString());
        return new Level(path);
    }

    [Test]
    public void NoDeclaredSongMeansNoSong()
    {
        // The folder holds audio, but the level never asked for any of it
        Level level = WriteLevel(string.Empty, "other-version.wav", "hitsound.ogg", "clip.mp4");

        Assert.That(LevelAudioLocator.Find(level, level.pathToLevel!), Is.Null);
    }

    [Test]
    public void DeclaredSongWins()
    {
        Level level = WriteLevel("song.wav", "song.wav", "a.wav", "b.ogg");

        string? found = LevelAudioLocator.Find(level, level.pathToLevel!);

        Assert.That(found, Is.Not.Null);
        Assert.That(Path.GetFileName(found), Is.EqualTo("song.wav"));
    }

    [Test]
    public void MissingDeclaredSongIsNotReplacedByAnotherFile()
    {
        // songFilename points at a file that is gone; a sibling track must not be used instead
        Level level = WriteLevel("gone.wav", "other.wav", "other.ogg");

        Assert.That(LevelAudioLocator.Find(level, level.pathToLevel!), Is.Null);
    }

    [Test]
    public void SameStemWithAnotherExtensionIsAccepted()
    {
        // song.mp3 declared, but only song.ogg shipped
        Level level = WriteLevel("song.mp3", "song.ogg");

        string? found = LevelAudioLocator.Find(level, level.pathToLevel!);

        Assert.That(found, Is.Not.Null);
        Assert.That(Path.GetFileName(found), Is.EqualTo("song.ogg"));
    }

    [Test]
    public void VideoFilesAreNeverTreatedAsAudio()
    {
        Assert.That(LevelAudioLocator.IsAudioFile("d4d.mp4"), Is.False);
        Assert.That(LevelAudioLocator.IsAudioFile("clip.webm"), Is.False);
        Assert.That(LevelAudioLocator.IsAudioFile("song.wav"), Is.True);
        Assert.That(LevelAudioLocator.IsAudioFile("song.ogg"), Is.True);
    }

    [Test]
    public void DeclaredVideoIsNotLoadedAsMusic()
    {
        // A level that names its background video in songFilename must not make it the music
        Level level = WriteLevel("petal.mp4", "petal.mp4");

        Assert.That(LevelAudioLocator.Find(level, level.pathToLevel!), Is.Null);
    }
}
