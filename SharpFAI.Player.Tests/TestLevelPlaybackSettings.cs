using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Player;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public class TestLevelPlaybackSettings
{
    [TestCase(.043, -43)]
    [TestCase(-.038, 38)]
    public void TapCalibrationUsesMedianAndRejectsWarmupOffBeatAndDuplicatePresses(double error, int expected)
    {
        AudioCalibrationWindow.TapMeasurement measurement = new();
        Assert.That(measurement.AddTap(1.5), Is.Null);
        Assert.That(measurement.AddTap(2.0), Is.Null);
        Assert.That(measurement.AddTap(2.75), Is.Null);
        Assert.That(measurement.Count, Is.Zero);

        int? result = null;
        for (int beat = 2; beat < 14; beat++)
        {
            double time = 1.5 + beat * .5;
            result = measurement.AddTap(time + (beat == 4 ? .18 : error));
            Assert.That(measurement.AddTap(time + .19), Is.Null);
            Assert.That(measurement.Count, Is.EqualTo(beat - 1));
        }

        Assert.That(result, Is.EqualTo(expected));
        measurement.Reset();
        Assert.That(measurement.Count, Is.Zero);
        measurement.AddTap(2.55);
        measurement.NewCycle();
        measurement.AddTap(2.55);
        Assert.That(measurement.Count, Is.EqualTo(2));
    }

    [Test]
    public void AuthoredValuesAreReadWithoutModifyingTheChart()
    {
        JObject values = JObject.Parse("""
            {"backgroundColor":"12345678","bgImage":"bg.png","bgDisplayMode":"Tiled",
             "parallax":[25,50],"zoom":300,"position":[2,-1],"rotation":30,"relativeTo":"Global",
             "volume":50,"hitsound":"None","hitsoundVolume":30,"tileShape":"Short",
             "planetEase":"InQuad","planetEaseParts":2}
            """);
        JObject original = (JObject)values.DeepClone();
        Level level = new(new Dictionary<string, object> { ["settings"] = values });
        LevelPlaybackSettings settings = LevelPlaybackSettings.Read(level);

        Assert.That(settings.Background, Is.EqualTo(new Vector4(0x12, 0x34, 0x56, 0x78) / 255f));
        Assert.That(settings.Display, Is.EqualTo(BackgroundDisplay.Tiled));
        Assert.That(settings.Parallax, Is.EqualTo(new Vector2(.25f, .5f)));
        Assert.That(settings.CameraZoom, Is.EqualTo(300f));
        Assert.That(settings.CameraOffset, Is.EqualTo(new Vector2(2f, -1f)));
        Assert.That(settings.CameraRelativeTo, Is.EqualTo(EventEnums.CamMovementType.Global));
        Assert.That(settings.MusicVolume, Is.EqualTo(.5f));
        Assert.That(settings.HitsoundVolume, Is.EqualTo(.3f));
        Assert.That(settings.Hitsound, Is.EqualTo("None"));
        Assert.That(settings.PlanetEase, Is.EqualTo(EventEnums.Ease.InQuad));
        Assert.That(settings.PlanetEaseParts, Is.EqualTo(2));
        Assert.That(settings.TileLength, Is.EqualTo(2f / 3f));
        Assert.That(JToken.DeepEquals(values, original), Is.True);
    }

    [Test]
    public void ExportMixUsesTheSamePlaybackRateAsVideoFrames()
    {
        byte[] pcm = new byte[12];
        Buffer.BlockCopy(new short[] { 0, 1000, 2000, 3000, 4000, 5000 }, 0, pcm, 0, pcm.Length);
        DecodedAudio source = new() { Pcm = pcm, SampleRate = 48000, Channels = 1 };
        string path = Path.GetTempFileName();
        try
        {
            VideoExporter.WriteMixedWav(source, null, .5f, 0f, 3.0 / 48000.0, path, playbackRate: 2);
            byte[] wav = File.ReadAllBytes(path);
            Assert.That(BitConverter.ToInt16(wav, 44), Is.EqualTo(0));
            Assert.That(BitConverter.ToInt16(wav, 48), Is.InRange(999, 1000));
            Assert.That(BitConverter.ToInt16(wav, 52), Is.InRange(1999, 2000));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void ExportMixStartsBothTracksAtTheUncalibratedOrigin()
    {
        short[] music = [1000, 2000, 3000, 4000, 5000, 6000];
        short[] hitsound = [500, 500, 500, 500, 500, 500];
        string path = Path.GetTempFileName();
        try
        {
            VideoExporter.WriteMixedWav(Clip(music), Clip(hitsound), 1f, 1f, 3.0 / 48000.0, path,
                playbackRate: 2);
            byte[] wav = File.ReadAllBytes(path);
            Assert.That(BitConverter.ToInt16(wav, 44), Is.EqualTo(1500).Within(1));
            Assert.That(BitConverter.ToInt16(wav, 48), Is.EqualTo(3500).Within(1));
            Assert.That(BitConverter.ToInt16(wav, 52), Is.EqualTo(5500).Within(1));
        }
        finally
        {
            File.Delete(path);
        }

        static DecodedAudio Clip(short[] samples)
        {
            byte[] pcm = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            return new DecodedAudio { Pcm = pcm, SampleRate = 48000, Channels = 1 };
        }
    }
}
