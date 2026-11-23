using Newtonsoft.Json.Linq;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public class TestEditorAudioReuse
{
    private static Level MakeLevel(string songFilename) => new(new Dictionary<string, object>
    {
        ["angleData"] = new JArray(0, 0),
        ["settings"] = new JObject { ["songFilename"] = songFilename },
    });

    private static (string? MusicPath, DecodedAudio? Music, DecodedAudio? Hitsound) Build(
        string songFilename, string levelPath, LevelRenderer.EditorAudioReuse reuse, bool reuseHitsound = true,
        LevelPlaybackSettings? settings = null)
        => LevelRenderer.BuildAudio(MakeLevel(songFilename).CreateSnapshot(levelPath), levelPath,
            [new Floor(0, 180, default), new Floor(0, 180, default)],
            new LevelRenderer.PlaybackTimeline { EntryTimes = [0.0, 0.1], LevelDuration = 0.6 },
            settings ?? new LevelPlaybackSettings(), reuse: reuse, reuseHitsound: reuseHitsound);

    [TestCase(false)]
    [TestCase(true)]
    public void ReusesDecodedMusicAndCachedHitsoundWhenResolvedPathMatches(bool silentHitsound)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, []);
        try
        {
            var music = new DecodedAudio();
            DecodedAudio? hitsound = silentHitsound ? null : new DecodedAudio();
            var result = Build(Path.GetFileName(path), Path.ChangeExtension(path, ".adofai"), new(path, music, hitsound));

            Assert.That(result.MusicPath, Is.EqualTo(path));
            Assert.That(result.Music, Is.SameAs(music));
            Assert.That(ReferenceEquals(result.Hitsound, hitsound), Is.True);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ChangedPathDecodesReplacementAndInvalidatesHitsoundReuse()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}.wav");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8);
            writer.Write(38);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(48000);
            writer.Write(96000);
            writer.Write((ushort)2);
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write(2);
            writer.Write((short)123);
        }

        try
        {
            var result = Build(Path.GetFileName(path), Path.ChangeExtension(path, ".adofai"),
                new(path + ".old", new DecodedAudio(), null));

            Assert.That(result.MusicPath, Is.EqualTo(path));
            Assert.That(result.Music?.Pcm, Is.EqualTo(new byte[] { 123, 0 }));
            Assert.That(result.Hitsound, Is.Not.Null);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void NoSongCanReuseSilentHitsoundButTimingInvalidationSynthesizes()
    {
        var reuse = new LevelRenderer.EditorAudioReuse(null, null, null);
        string chart = Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}.adofai");

        var result = Build("", chart, reuse);
        Assert.That(result.MusicPath, Is.Null);
        Assert.That(result.Music, Is.Null);
        Assert.That(result.Hitsound, Is.Null);
        Assert.That(Build("", chart, reuse, reuseHitsound: false).Hitsound, Is.Not.Null);
    }

    [TestCase(0.5)]
    [TestCase(1.5)]
    public void RateChangeRetimesOnlyOnsetsAndKeepsOriginalKickWaveform(double rate)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}.adofai");
        Level level = MakeLevel("").CreateSnapshot(path);
        List<Floor> floors = [new Floor(0, 180, default), new Floor(0, 180, default)];
        var timeline = new LevelRenderer.PlaybackTimeline { EntryTimes = [0.0, 0.375], LevelDuration = 1.0 };
        var settings = new LevelPlaybackSettings();
        DecodedAudio normal = LevelRenderer.BuildAudio(level, path, floors, timeline, settings).Hitsound!;
        var reuse = new LevelRenderer.EditorAudioReuse(null, null, normal, timeline.Pitch);
        DecodedAudio changed = LevelRenderer.BuildAudio(level, path, floors, timeline with { Pitch = rate }, settings,
            reuse: reuse, reuseHitsound: true).Hitsound!;

        Assert.That(changed, Is.Not.SameAs(normal));
        Assert.That(changed.SampleRate, Is.EqualTo(normal.SampleRate));
        Assert.That(changed.Channels, Is.EqualTo(normal.Channels));
        int stride = normal.Channels * sizeof(short);
        int normalStart = (int)(timeline.EntryTimes[1] * normal.SampleRate) * stride;
        int changedStart = (int)(timeline.EntryTimes[1] / rate * changed.SampleRate) * stride;
        Assert.That(changed.Pcm.AsSpan(0, changedStart).ContainsAnyExcept((byte)0), Is.False);
        Assert.That(changed.Pcm.AsSpan(changedStart).SequenceEqual(normal.Pcm.AsSpan(normalStart)), Is.True);
    }

    [Test]
    public void SwitchingRatesAndUndoingATimingChangeReusePreviouslySynthesizedPcm()
    {
        const string path = "rate-preview.adofai";
        Level level = MakeLevel("").CreateSnapshot(path);
        List<Floor> floors = [new Floor(0, 180, default), new Floor(0, 180, default)];
        var settings = new LevelPlaybackSettings();
        LevelRenderer.EditorAudioReuse reuse = default;

        DecodedAudio Preview(double rate, double landing, bool timingUnchanged = true)
        {
            var timeline = new LevelRenderer.PlaybackTimeline { EntryTimes = [0.0, landing], Pitch = rate };
            var result = LevelRenderer.BuildAudio(level, path, floors, timeline, settings,
                reuse: reuse, reuseHitsound: timingUnchanged);
            Assert.That(result.Hitsound, Is.Not.Null);
            reuse = new(result.MusicPath, result.Music, result.Hitsound, rate);
            return result.Hitsound!;
        }

        DecodedAudio normal = Preview(1.0, 0.431);
        DecodedAudio slow = Preview(0.5, 0.431);
        Assert.That(slow, Is.Not.SameAs(normal));
        Assert.That(Preview(1.0, 0.431), Is.SameAs(normal));
        Assert.That(Preview(0.5, 0.431), Is.SameAs(slow));
        Assert.That(Preview(0.5, 0.571, timingUnchanged: false), Is.Not.SameAs(slow));
        Assert.That(Preview(0.5, 0.431, timingUnchanged: false), Is.SameAs(slow));
    }

    [Test]
    public void NoneHitsoundPresetDoesNotReuseOldPcm()
    {
        var settings = new LevelPlaybackSettings { Hitsound = "None" };
        var result = Build("", "level.adofai", new(null, null, new DecodedAudio()), settings: settings);

        Assert.That(result.Hitsound, Is.Null);
    }

    [Test]
    public void SkipAudioDoesNotLocateDecodeOrReuseAudio()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, []);
        try
        {
            string chart = Path.ChangeExtension(path, ".adofai");
            var reuse = new LevelRenderer.EditorAudioReuse(path, new DecodedAudio(), new DecodedAudio());
            Assert.That(Build(Path.GetFileName(path), chart, reuse).Music, Is.SameAs(reuse.Music));
            var result = Build(Path.GetFileName(path), chart, reuse, settings: new LevelPlaybackSettings { SkipAudio = true });
            Assert.That(result, Is.EqualTo(default((string?, DecodedAudio?, DecodedAudio?))));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
