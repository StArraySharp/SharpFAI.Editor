using System.Diagnostics;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly bool _profileStartup = Environment.GetEnvironmentVariable("SHARPFAI_PROFILE_STARTUP") == "1";
    private readonly Stopwatch _startupClock = Stopwatch.StartNew();
    private bool _loggedStartupLoad;
    private int _startupSlowFrames;

    private void ProfileStartupFrame(long start, long guiReady, long sceneReady, long uiReady)
    {
        if (_playback is { Level: not null } renderer && !_loggedStartupLoad)
        {
            _loggedStartupLoad = true;
            foreach (var (phase, milliseconds) in renderer.LoadTimings)
                Console.WriteLine($"Startup load {phase}: {milliseconds:F1} ms");
        }

        long end = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(start, end).TotalMilliseconds < 25 || _startupSlowFrames++ >= 20)
            return;
        Console.WriteLine($"Startup frame at {_startupClock.Elapsed.TotalSeconds:F2}s: " +
            $"ImGui setup {Stopwatch.GetElapsedTime(start, guiReady).TotalMilliseconds:F1} ms, " +
            $"load/update {Stopwatch.GetElapsedTime(guiReady, sceneReady).TotalMilliseconds:F1} ms, " +
            $"panels/scene render {Stopwatch.GetElapsedTime(sceneReady, uiReady).TotalMilliseconds:F1} ms, " +
            $"UI render/swap {Stopwatch.GetElapsedTime(uiReady, end).TotalMilliseconds:F1} ms");
    }
}
