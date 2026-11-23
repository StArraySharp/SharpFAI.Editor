using System.Text.Json;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace SharpFAI.Player;

public sealed record ApplicationSettings
{
    public bool VSync { get; init; }
    public int FrameLimit { get; init; }
    public bool ShowFrameRate { get; init; } = true;
    public int MasterVolume { get; init; } = 100;
    public int MusicVolume { get; init; } = 100;
    public int HitsoundVolume { get; init; } = 100;
    public bool SkipAudio { get; init; }
    public string? Language { get; init; }

    public static ApplicationSettings PlayerDefaults { get; } = new();
    public static ApplicationSettings EditorDefaults { get; } = new() { VSync = true };

    public ApplicationSettings Normalize() => this with
    {
        FrameLimit = FrameLimit is 30 or 60 or 120 or 144 or 240 ? FrameLimit : 0,
        MasterVolume = Math.Clamp(MasterVolume, 0, 100),
        MusicVolume = Math.Clamp(MusicVolume, 0, 100),
        HitsoundVolume = Math.Clamp(HitsoundVolume, 0, 100),
        Language = Language is "en" or "zh-Hans" ? Language : null,
    };

    public void ApplyFramePacing(GameWindow window)
    {
        window.VSync = VSync ? VSyncMode.On : VSyncMode.Off;
        window.UpdateFrequency = FrameLimit;
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
            return JsonSerializer.Deserialize<ApplicationSettings>(file)?.Normalize() ?? defaults;
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
