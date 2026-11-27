using ImGui4NET;

namespace SharpFAI.Player;

internal sealed class ImGuiLayoutStore(string path)
{
    internal static ImGuiLayoutStore ForApplication(bool editor) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpFAI",
        editor ? "editor-imgui.ini" : "player-imgui.ini"));

    internal string? Load()
    {
        try
        {
            string layout = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(layout)) return null;
            ImGui.LoadIniSettingsFromMemory(layout, 0);
            return layout;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Layout] Could not load window layout: {error.Message}");
            return null;
        }
    }

    internal unsafe void Save(string? comment = null)
    {
        if (ImGui.GetFrameCount() == 0) return;
        nuint length;
        sbyte* layout = ImGui.SaveIniSettingsToMemory(&length);
        if (layout == null || length == 0) return;

        string? temporary = null;
        try
        {
            string destination = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string candidate = $"{destination}.{Guid.NewGuid():N}.tmp";
            using (FileStream file = File.Open(candidate, FileMode.CreateNew, FileAccess.Write))
            {
                temporary = candidate;
                file.Write(new ReadOnlySpan<byte>(layout, checked((int)length)));
                if (comment is not null)
                {
                    using StreamWriter writer = new(file);
                    writer.WriteLine();
                    writer.WriteLine(comment);
                }
            }
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Layout] Could not save window layout: {error.Message}");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
