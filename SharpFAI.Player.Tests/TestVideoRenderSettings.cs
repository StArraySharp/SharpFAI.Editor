using SharpFAI.Serialization;
using SharpFAI.Player.Engine;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public sealed class TestVideoRenderSettings
{
    [Test]
    public void WindowExportUsesTheChartCameraZoom()
    {
        var settings = new VideoRenderWindow.Settings(1920, 1080, 60, PreferHardware: false);
        var request = settings.CreateRequest(Path.Combine(Path.GetTempPath(), "camera-zoom.adofai"), Level.CreateNewLevel());

        Assert.That(request.Zoom, Is.Zero, "A fixed zoom would override the chart's MoveCamera events.");
    }

    [Test]
    public void SelectionStartsAtAAndWarmsEarlierFramesOnTheOutputClock()
    {
        var timing = VideoExportTiming.Create(new PlaybackRange(1.2, 2.2), 5, 1, 60, 2);
        Assert.Multiple(() =>
        {
            Assert.That(timing.PrerollFrames, Is.EqualTo(72));
            Assert.That(timing.TimeAt(-timing.PrerollFrames), Is.EqualTo(0).Within(1e-9));
            Assert.That(timing.TimeAt(-1), Is.EqualTo(1.2 - 1d / 60).Within(1e-9));
            Assert.That(timing.TimeAt(0), Is.EqualTo(1.2));
            Assert.That(timing.FrameCount, Is.EqualTo(60));
            Assert.That(timing.TimeAt(59), Is.LessThan(2.2));
        });
    }

    [Test]
    public void SelectionMixStartsAtTheMusicAndOutputHitsoundOffsets()
    {
        byte[] ToPcm(short[] samples)
        {
            var bytes = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        var music = new DecodedAudio { SampleRate = 48000, Channels = 1, Pcm = ToPcm([0, 1000, 2000, 3000, 4000]) };
        var hits = new DecodedAudio { SampleRate = 48000, Channels = 1, Pcm = ToPcm([0, 20, 40, 60]) };
        string path = Path.GetTempFileName();
        try
        {
            VideoExporter.WriteMixedWav(music, hits, 1, 1, 2d / 48000, path, playbackRate: 2, hitsoundRate: 1,
                musicStart: 2d / 48000, hitsoundStart: 1d / 48000);
            byte[] wav = File.ReadAllBytes(path);
            Assert.That(wav.Length, Is.EqualTo(52));
            Assert.That(BitConverter.ToInt16(wav, 44), Is.EqualTo(2020).Within(1));
            Assert.That(BitConverter.ToInt16(wav, 48), Is.EqualTo(4040).Within(1));
        }
        finally { File.Delete(path); }
    }
}
