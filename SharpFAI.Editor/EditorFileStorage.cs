using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Editor;

internal static class EditorFileStorage
{
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpFAI");

    public static void WriteChart(string destination, JObject chart, bool overwrite) =>
        WriteAtomically(destination, writer =>
        {
            using JsonTextWriter json = new(writer) { Formatting = Formatting.Indented };
            chart.WriteTo(json);
        }, overwrite);

    public static void WriteAtomically(string destination, Action<TextWriter> write, bool overwrite)
    {
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".sharpfai-{Guid.NewGuid():N}.tmp");
        bool ownsTemp = false;
        try
        {
            using (FileStream file = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemp = true;
                using StreamWriter writer = new(file);
                write(writer);
            }
            File.Move(temp, destination, overwrite);
            ownsTemp = false;
        }
        finally
        {
            if (ownsTemp)
            {
                try { File.Delete(temp); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}

internal sealed class LastOpenedChart(string recordPath)
{
    public string? Read()
    {
        try
        {
            string chart = File.ReadAllText(recordPath);
            return Path.IsPathFullyQualified(chart) ? Path.GetFullPath(chart) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Remember(string chart)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
            EditorFileStorage.WriteAtomically(recordPath, writer => writer.Write(chart), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[Editor] Could not remember the last chart: {error.Message}");
        }
    }
}
