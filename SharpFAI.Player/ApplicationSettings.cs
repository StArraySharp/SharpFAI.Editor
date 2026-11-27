using System.Globalization;
using System.Text.Json;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using DisplayMonitor = OpenTK.Windowing.GraphicsLibraryFramework.Monitor;

namespace SharpFAI.Player;

[Flags]
public enum DecorationKinds
{
    None = 0,
    Image = 1,
    Text = 2,
    Floor = 4,
    Planet = 8,
    Particle = 16,
    PlayerBubble = 32,
    All = Image | Text | Floor | Planet | Particle | PlayerBubble,
}

public enum ApplicationWindowMode { Windowed, Fullscreen }

[Flags]
public enum SelectionStatisticsFields { None = 0, Angle = 1, Beats = 2, Count = 4, Duration = 8, All = Angle | Beats | Count | Duration }

public sealed record ApplicationSettings
{
    public const string DefaultFireColor = "#FF0000";
    public const string DefaultIceColor = "#0000FF";

    public bool VSync { get; init; }
    public ApplicationWindowMode WindowMode { get; init; }
    public int FrameLimit { get; init; }
    public bool ShowFrameRate { get; init; } = true;
    public bool ShowTimelineTimingEvents { get; init; } = true;
    public bool ShowTimelineEffectEvents { get; init; } = true;
    public string FirePlanetColor { get; init; } = DefaultFireColor;
    public string IcePlanetColor { get; init; } = DefaultIceColor;
    public DecorationKinds VisibleDecorations { get; init; } = DecorationKinds.All;
    public int DecorationTexturePercent { get; init; } = 100;
    public int PlanetTrailPercent { get; init; } = 100;
    public bool CameraFilters { get; init; } = true;
    public bool ScreenEffects { get; init; } = true;
    public bool ScreenFlash { get; init; } = true;
    public bool CameraShake { get; init; } = true;
    public int MasterVolume { get; init; } = 100;
    public int MusicVolume { get; init; } = 100;
    public int HitsoundVolume { get; init; } = 100;
    public bool SkipAudio { get; init; }
    public string? Language { get; init; }
    public bool ShowSelectionStatistics { get; init; } = true;
    public SelectionStatisticsFields SelectionStatistics { get; init; } = SelectionStatisticsFields.All;
    public bool ExcludeLastSelectedTileFromTiming { get; init; }
    public bool FineTrackRotation { get; init; } = true;
    public float TrackRotationStep { get; init; } = 22.5f;
    public bool SyncTrackEventPositions { get; init; }
    public bool HidePlanetsInTilesOnly { get; init; } = true;

    public static ApplicationSettings PlayerDefaults { get; } = new();
    public static ApplicationSettings EditorDefaults { get; } = new() { VSync = true };

    public ApplicationSettings Normalize() => this with
    {
        WindowMode = WindowMode == ApplicationWindowMode.Fullscreen ? WindowMode : ApplicationWindowMode.Windowed,
        FrameLimit = FrameLimit is 30 or 60 or 120 or 144 or 240 ? FrameLimit : 0,
        FirePlanetColor = TryParsePlanetColor(FirePlanetColor, out int fire) ? FormatPlanetColor(fire) : DefaultFireColor,
        IcePlanetColor = TryParsePlanetColor(IcePlanetColor, out int ice) ? FormatPlanetColor(ice) : DefaultIceColor,
        VisibleDecorations = VisibleDecorations & DecorationKinds.All,
        DecorationTexturePercent = DecorationTexturePercent is 25 or 50 or 75 or 100 ? DecorationTexturePercent : 100,
        PlanetTrailPercent = Math.Clamp(PlanetTrailPercent, 0, 200),
        MasterVolume = Math.Clamp(MasterVolume, 0, 100),
        MusicVolume = Math.Clamp(MusicVolume, 0, 100),
        HitsoundVolume = Math.Clamp(HitsoundVolume, 0, 100),
        Language = Language is "en" or "zh-Hans" ? Language : null,
        SelectionStatistics = SelectionStatistics & SelectionStatisticsFields.All,
        TrackRotationStep = float.IsFinite(TrackRotationStep) ? Math.Clamp(TrackRotationStep, .001f, 360f) : 22.5f,
    };

    internal static bool TryParsePlanetColor(string? value, out int rgb)
    {
        ReadOnlySpan<char> hex = value.AsSpan().Trim();
        if (!hex.IsEmpty && hex[0] == '#')
            hex = hex[1..];
        rgb = 0;
        return hex.Length == 6 && int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    internal static string FormatPlanetColor(int rgb) => $"#{rgb:X6}";

    public void ApplyFramePacing(GameWindow window)
    {
        window.VSync = VSync ? VSyncMode.On : VSyncMode.Off;
        window.UpdateFrequency = GetFrameLimit(window);
    }

    internal int GetFrameLimit(GameWindow window) => EffectiveFrameLimit(VSync ? ReadRefreshRate(window) : 0);

    internal int EffectiveFrameLimit(int refreshRate) => VSync && refreshRate > 0
        ? FrameLimit > 0 ? Math.Min(FrameLimit, refreshRate) : refreshRate
        : FrameLimit;

    private static unsafe int ReadRefreshRate(GameWindow window)
    {
        DisplayMonitor* monitor = GLFW.GetWindowMonitor(window.WindowPtr);
        if (monitor == null && Monitors.GetMonitorFromWindow(window) is { } display)
            monitor = display.Handle.ToUnsafePtr<DisplayMonitor>();
        if (monitor == null)
            return 0;
        var mode = GLFW.GetVideoMode(monitor);
        return mode != null ? mode->RefreshRate : 0;
    }
}

public sealed class ApplicationSettingsStore(string path, ApplicationSettings defaults)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ApplicationSettingsStore ForApplication(bool editor) => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpFAI", editor ? "editor-settings.json" : "player-settings.json"),
        editor ? ApplicationSettings.EditorDefaults : ApplicationSettings.PlayerDefaults);

    public ApplicationSettings Load()
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(file);
            var root = document.RootElement;
            if (root.Deserialize<ApplicationSettings>() is not { } settings)
                return defaults;
            if (root.TryGetProperty("WhitePlanetBodies", out var white) && white.ValueKind == JsonValueKind.True)
            {
                settings = settings with
                {
                    FirePlanetColor = root.TryGetProperty(nameof(ApplicationSettings.FirePlanetColor), out _)
                        ? settings.FirePlanetColor : "#FFFFFF",
                    IcePlanetColor = root.TryGetProperty(nameof(ApplicationSettings.IcePlanetColor), out _)
                        ? settings.IcePlanetColor : "#FFFFFF",
                };
            }
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[Settings] Could not read preferences: {ex.Message}");
            return defaults;
        }
    }

    public bool TrySave(ApplicationSettings settings)
    {
        string? temporary = null;
        try
        {
            string destination = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string candidate = $"{destination}.{Guid.NewGuid():N}.tmp";
            using (FileStream file = File.Open(candidate, FileMode.CreateNew, FileAccess.Write))
            {
                temporary = candidate;
                JsonSerializer.Serialize(file, settings.Normalize(), JsonOptions);
            }
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Settings] Could not save preferences: {ex.Message}");
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
