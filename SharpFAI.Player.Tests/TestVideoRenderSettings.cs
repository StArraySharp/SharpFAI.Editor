using SharpFAI.Serialization;

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
}
