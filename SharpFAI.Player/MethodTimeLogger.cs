using System.Reflection;

namespace SharpFAI.Player;

/// <summary>
/// Interceptor that <c>MethodTimer.Fody</c> picks up by convention: every <c>[Time]</c> method
/// reports its duration here. The player runs as a GUI-subsystem process whose stdout is not
/// attached, so timings go to a file that a <c>--shot</c> run can inspect afterwards.
/// </summary>
public static class MethodTimeLogger
{
    private static readonly object Gate = new();

    /// <summary>Only collect timings when <c>SHARPFAI_TIMING</c> is set, keeping normal runs quiet.</summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("SHARPFAI_TIMING") is { Length: > 0 };

    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "sharpfai-methodtimer.log");

    public static void Log(MethodBase methodBase, TimeSpan elapsed, string message)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path,
                    $"{methodBase.DeclaringType?.Name}.{methodBase.Name}\t{elapsed.TotalMilliseconds:F2}ms{message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Tracing must never break playback.
            }
        }
    }
}
