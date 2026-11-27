using System.Diagnostics;
using ImGui4NET;
using SharpFAI.Player.Engine;

namespace SharpFAI.Player.Tests;

public sealed class TestPlaybackPerformance
{
    [Test]
    public unsafe void GraphZoomKeepsTheHoveredSampleAndSeeksWithinTheVisibleRange()
    {
        ImGuiContext* previous = ImGui.GetCurrentContext();
        ImGuiContext* context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        try
        {
            ImGuiIO* io = ImGui.GetIO();
            io->IniFilename = null;
            io->DisplaySize = new ImVec2 { x = 640, y = 480 };
            io->BackendFlags |= (int)ImGuiBackendFlags.ImGuiBackendFlags_RendererHasTextures;
            var window = new PlaybackPerformanceWindow(key => key) { IsOpen = true };
            object source = new();
            for (int floor = 0; floor <= 100; floor++)
            {
                using var frame = window.BeginFrame(source, playback: true);
                PlaybackPerformance.MarkScene(floor, floor);
                frame.Present();
            }

            const float anchor = 164;
            const float right = 436;
            int selected = -1;
            Draw(anchor);
            Draw(anchor, down: true);
            int anchorFloor = selected;
            Draw(right);
            Draw(right, down: true);
            int rightFloor = selected;
            Assert.That(rightFloor - anchorFloor, Is.InRange(48, 52));

            Draw(anchor);
            Draw(anchor, wheel: 4);
            Draw(anchor, down: true);
            Assert.That(selected, Is.EqualTo(anchorFloor).Within(1));
            Draw(right);
            Draw(right, down: true);
            Assert.That(selected - anchorFloor, Is.InRange(21, 24));

            Draw(anchor);
            Draw(anchor, wheel: -4);
            Draw(right);
            Draw(right, down: true);
            Assert.That(selected, Is.EqualTo(rightFloor).Within(1));

            void Draw(float x, bool down = false, float wheel = 0)
            {
                ImGuiIO.AddMousePosEvent(io, x, 145);
                ImGuiIO.AddMouseButtonEvent(io, (int)ImGuiMouseButton.ImGuiMouseButton_Left, (byte)(down ? 1 : 0));
                if (wheel != 0) ImGuiIO.AddMouseWheelEvent(io, 0, wheel);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new ImVec2 { x = 20, y = 20 }, (int)ImGuiCond.ImGuiCond_Always, default);
                window.Draw(sample => selected = sample.Floor);
                ImGui.EndFrame();
            }
        }
        finally
        {
            ImGui.DestroyContext(context);
            ImGui.SetCurrentContext(previous);
        }
    }

    [Test]
    public void PresentedFrameTracksItsSceneAndSkipsPausedTime()
    {
        var history = new PlaybackPerformance();
        long step = Stopwatch.Frequency / 100;
        Present(step, 1.0, 20);
        Present(2 * step, 1.01, 21);
        using (history.BeginFrame(capture: false)) { }
        Present(1000 * step, 5, 42);
        Present(1003 * step, 5.03, 43);

        Assert.That(history.Count, Is.EqualTo(2));
        Assert.That(history[0].Interval, Is.EqualTo(10).Within(.01));
        Assert.That(history[1].Time, Is.EqualTo(5.03));
        Assert.That(history[1].Floor, Is.EqualTo(43));
        Assert.That(history.Percentiles().P99, Is.EqualTo(30).Within(.01));

        void Present(long stamp, double time, int floor)
        {
            using var frame = history.BeginFrame(capture: true);
            PlaybackPerformance.MarkScene(time, floor);
            history.Present(stamp);
        }
    }
}
