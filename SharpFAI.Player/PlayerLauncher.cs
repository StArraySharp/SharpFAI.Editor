using System.Globalization;
using System.Reflection;
using System.Runtime;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Player;

internal static class PlayerLauncher
{
    internal const string Usage = """
        SharpFAI.Editor --player [level.adofai | --open <level>] [--no-audio] [--no-mods]
          --shot <png> [--time <seconds>] [--select <floor>] [--zoom <scale>]
          --video <mp4> [--size <width>x<height>] [--fps <fps>] [--bitrate <kbps>]
                       [--keep-frames] [--hide-passed | --keep-passed] [--software-encoding]
          --fire-color <RRGGBB> --ice-color <RRGGBB>
        """;

    internal static int Run(string[] args, Assembly hostAssembly, Func<bool, ModSupport.IModRuntime> createMods)
    {
        if (args is ["--help" or "-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var options = new LaunchOptions();
        if (Parse(args, options) is { } error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        PlaybackAudio.Configure(hostAssembly, options.SkipAudio);
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Shader.SourceResolver = ShaderLibrary.ResolveForEngine;

        using var window = new PlayerWindow(GameWindowSettings.Default, new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "SharpFAI Player",
            Icon = ApplicationIcon.Load(),
            StartVisible = false,
            APIVersion = new Version(4, 1),
            Profile = ContextProfile.Core,
            Vsync = VSyncMode.Off,
            DepthBits = 24,
            StencilBits = 8,
        })
        {
            PendingLevelPath = options.LevelPath,
            CapturePath = options.CapturePath,
            CaptureZoom = options.Zoom,
            CaptureSelectFloor = options.Floor,
            CaptureTime = options.Time,
            SkipAudioFromCommandLine = options.SkipAudio,
            CreateMods = () => createMods(options.DisableMods),
            FireColorFromCommandLine = options.FireColor,
            IceColorFromCommandLine = options.IceColor,
        };

        if (options.VideoPath is { } video)
        {
            var export = new VideoExporter.Request(options.LevelPath!, video, options.Width, options.Height,
                options.Fps, options.KeepFrames, options.Bitrate, options.Zoom, options.HidePassed, options.PreferHardware);
            Console.WriteLine($"export queued: {export.LevelPath} -> {export.OutputPath} {export.Width}x{export.Height}@{export.Fps}");
            window.ExportRequest = export;
        }
        else
        {
            window.IsVisible = true;
        }

        window.Run();
        return window.CommandExitCode;
    }

    private static string? Parse(string[] args, LaunchOptions options)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i];
            switch (flag)
            {
                case "--no-audio": options.SkipAudio = true; continue;
                case "--no-mods": options.DisableMods = true; continue;
                case "--keep-frames": options.KeepFrames = true; continue;
                case "--hide-passed": options.HidePassed = true; continue;
                case "--keep-passed": options.HidePassed = false; continue;
                case "--software-encoding": options.PreferHardware = false; continue;
            }

            if (!flag.StartsWith('-') && options.LevelPath is null)
            {
                options.LevelPath = flag;
                continue;
            }

            string? value = i + 1 < args.Length ? args[++i] : null;
            switch (flag)
            {
                case "--open" or "-o" when IsPath(value): options.LevelPath = value; break;
                case "--shot" or "-s" when IsPath(value): options.CapturePath = value; break;
                case "--video" or "-v" when IsPath(value): options.VideoPath = value; break;
                case "--size" when TryParseSize(value, out int width, out int height):
                    options.Width = width;
                    options.Height = height;
                    break;
                case "--fps" when int.TryParse(value, out int fps) && fps > 0: options.Fps = fps; break;
                case "--bitrate" when int.TryParse(value, out int bitrate) && bitrate >= 0: options.Bitrate = bitrate; break;
                case "--select" or "-S" when int.TryParse(value, out int floor): options.Floor = floor; break;
                case "--zoom" or "-z" when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float zoom)
                    && float.IsFinite(zoom):
                    options.Zoom = zoom;
                    break;
                case "--time" or "-t" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double time)
                    && double.IsFinite(time):
                    options.Time = time;
                    break;
                case "--fire-color" or "--ice-color":
                    if (!ApplicationSettings.TryParsePlanetColor(value, out int rgb))
                        return $"{flag} needs a six-digit RGB color, for example FFFFFF.";
                    if (flag == "--fire-color") options.FireColor = ApplicationSettings.FormatPlanetColor(rgb);
                    else options.IceColor = ApplicationSettings.FormatPlanetColor(rgb);
                    break;
                default:
                    return $"Invalid player argument or value: {flag}";
            }
        }

        if ((options.CapturePath is not null || options.VideoPath is not null) &&
            string.IsNullOrWhiteSpace(options.LevelPath))
            return "--shot and --video require a level path.";
        return null;
    }

    private static bool IsPath(string? value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith('-');

    private static bool TryParseSize(string? value, out int width, out int height)
    {
        width = height = 0;
        return value?.Split('x', 'X', '*') is [string x, string y] &&
            int.TryParse(x, out width) && int.TryParse(y, out height) && width > 0 && height > 0;
    }

    private sealed class LaunchOptions
    {
        public string? LevelPath, CapturePath, VideoPath, FireColor, IceColor;
        public int Width = 1920, Height = 1080, Fps = 60, Floor = -1, Bitrate;
        public double Time = -1;
        public float Zoom;
        public bool SkipAudio, KeepFrames, HidePassed, DisableMods;
        public bool PreferHardware = true;
    }
}
