using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;

namespace SharpFAI.Player.Tests;

public sealed class TestVideoBackground
{
    [Test]
    public void VideoUsesChartAndCountdownOffsetsAndLoopsOnItsOwnDuration()
    {
        var video = new VideoBackgroundSource("clip.mp4", "ffmpeg", 1920, 1080, 16d / 9, 4, 0, 0, 60);
        var settings = LevelPlaybackSettings.Read(JObject.Parse("""{"vidOffset":250,"loopVideo":true}"""));
        Assert.Multiple(() =>
        {
            Assert.That(settings.VideoOffset, Is.EqualTo(.25));
            Assert.That(settings.LoopVideo, Is.True);
            Assert.That(video.TimeAt(0, .5, 1, settings.VideoOffset, true), Is.EqualTo(-1.25));
            Assert.That(video.TimeAt(2.25, .5, 1, settings.VideoOffset, true), Is.EqualTo(1));
            Assert.That(video.TimeAt(6.25, .5, 1, settings.VideoOffset, true), Is.EqualTo(1));
            Assert.That(video.TimeAt(6.25, .5, 1, settings.VideoOffset, false), Is.EqualTo(4));
            Assert.That(LevelRenderer.EditorSettingLayers("vidOffset"), Is.EqualTo(LevelRenderer.EditorLayers.Backdrop));
            Assert.That(LevelRenderer.EditorSettingLayers("loopVideo"), Is.EqualTo(LevelRenderer.EditorLayers.Backdrop));
        });
    }

    [Test]
    public void MissingBackgroundVideoKeepsTheStaticFallback()
    {
        List<string> warnings = [];
        var settings = new LevelPlaybackSettings { BackgroundVideo = $"missing-{Guid.NewGuid():N}.mp4", ShowPatternTiles = true };
        var backdrop = BackdropTimeline.Build(settings, [], [], [], null, Path.GetTempPath(), warnings);
        Assert.Multiple(() =>
        {
            Assert.That(backdrop.Video, Is.Null);
            Assert.That(backdrop.NeedsBackgroundRenderer, Is.True);
            Assert.That(backdrop.BackgroundAt(10).ShowsDefaultPattern, Is.True);
            Assert.That(warnings, Has.Count.EqualTo(1));
        });
    }
}
