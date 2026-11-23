using System.Runtime;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Player;

public static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--filter-report")
        {
            Console.WriteLine("Component\tConversion\tResources\tGL\tVisual\tDetail");
            foreach (string row in Engine.Filters.GeneratedFilterRegistry.Coverage)
                Console.WriteLine(row);
            return;
        }

        // A long-running player benefits from SustainedLowLatency: fewer disruptive Gen2 pauses
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // Engine renderers read shaders from disk. A single-file bundle has no such folder, so point
        // them at the embedded copies; the on-disk path still wins when it exists.
        Shader.SourceResolver = static name => ShaderLibrary.ResolveForEngine(name);

        GameWindowSettings gameWindowSettings = GameWindowSettings.Default;
        NativeWindowSettings nativeWindowSettings = new()
        {
            ClientSize = new Vector2i(1280, 800),
            Title = "SharpFAI Player",
            StartVisible = false,
            APIVersion = new Version(4, 1),
            Profile = ContextProfile.Core,
            Vsync = VSyncMode.Off,
            DepthBits = 24,
            StencilBits = 8,
        };

        using PlayerWindow window = new(gameWindowSettings, nativeWindowSettings);

        // 视频导出参数，需在关卡与输出路径都确定后才能组装
        // 默认 1920x1080@60，与常见成片规格一致
        // Video export options, assembled once the level and output paths are known.
        // Defaults to 1920x1080@60, matching the usual delivery format
        string? exportOutput = null;
        int exportWidth = 1920;
        int exportHeight = 1080;
        int exportFps = 60;
        bool exportKeepFrames = false;
        int exportBitrate = 0;
        bool exportHidePassed = false;
        bool exportPreferHardware = true;
        bool skipAudio = false;

        // Allow loading a level straight from the command line
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--keep-passed" or "--hide-passed")
            {
                exportHidePassed = args[i] == "--hide-passed";
                continue;
            }

            if (args[i] == "--keep-frames")
            {
                exportKeepFrames = true;
                continue;
            }

            if (args[i] == "--software-encoding")
            {
                exportPreferHardware = false;
                continue;
            }

            if (args[i] == "--no-audio")
            {
                skipAudio = true;
                continue;
            }
            if (i + 1 >= args.Length)
            {
                continue;
            }

            if (args[i] is "--open" or "-o")
            {
                window.PendingLevelPath = args[i + 1];
                i++;
            }
            else if (args[i] is "--shot" or "-s")
            {
                window.CapturePath = args[i + 1];
                i++;
            }
            else if (args[i] is "--video" or "-v")
            {
                exportOutput = args[i + 1];
                i++;
            }
            else if (args[i] is "--size" && TryParseSize(args[i + 1], out int width, out int height))
            {
                exportWidth = width;
                exportHeight = height;
                i++;
            }
            else if (args[i] is "--fps" && int.TryParse(args[i + 1], out int fps))
            {
                exportFps = fps;
                i++;
            }
            else if (args[i] is "--bitrate" && int.TryParse(args[i + 1], out int bitrate))
            {
                exportBitrate = bitrate;
                i++;
            }
            else if (args[i] is "--zoom" or "-z"
                     && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out float zoom))
            {
                window.CaptureZoom = zoom;
                i++;
            }
            else if (args[i] is "--select" or "-S"
                     && int.TryParse(args[i + 1], out int selectFloor))
            {
                window.CaptureSelectFloor = selectFloor;
                i++;
            }
            else if (args[i] is "--time" or "-t"
                     && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out double captureTime))
            {
                window.CaptureTime = captureTime;
                i++;
            }
            else if (args[i] is "--convert-shaders")
            {
                // 需要 GL 上下文，所以延后到窗口 Run 之后由 PlayerWindow 执行
                // Needs a GL context, so PlayerWindow runs it once the window is current
                if (i + 2 < args.Length)
                {
                    window.PendingShaderConversion = (args[i + 1], args[i + 2]);
                    i += 2;
                }
            }
            else if (args[i] is "--verify-shaders")
            {
                // 校验某个目录下所有 .vert/.frag 对能否被引擎编译
                // Verify every .vert/.frag pair under a directory compiles
                window.PendingShaderVerification = args[i + 1];
                i++;
            }
        }

        // 视频导出需要在关卡加载完成后再开始，所以先记下请求，由窗口自己接管
        // Video export has to start after the level loads, so record the request and let the window
        // take over from there
        window.SkipAudioFromCommandLine = skipAudio;
        VideoExporter.Request? exportRequest = exportOutput is null || window.PendingLevelPath is null
            ? null
            : new VideoExporter.Request(
                window.PendingLevelPath,
                exportOutput,
                exportWidth,
                exportHeight,
                exportFps,
                exportKeepFrames,
                exportBitrate,
                window.CaptureZoom,
                exportHidePassed,
                exportPreferHardware);

        if (exportRequest is { } export)
        {
            Console.Out.WriteLine(
                $"export queued: {export.LevelPath} -> {export.OutputPath} {export.Width}x{export.Height}@{export.Fps}");
            window.ExportRequest = export;
        }
        else
        {
            window.IsVisible = true;
        }

        window.Run();
    }

    /// <summary>
    /// 解析 <c>1920x1080</c> 形式的尺寸。
    /// Parse a size written as `1920x1080`.
    /// </summary>
    private static bool TryParseSize(string text, out int width, out int height)
    {
        width = 0;
        height = 0;

        string[] parts = text.Split('x', 'X', '*');

        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height)
               && width > 0 && height > 0;
    }
}
