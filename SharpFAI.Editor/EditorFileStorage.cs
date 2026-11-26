using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using System.Security.Cryptography;

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

    internal static void SaveChartWithAssets(string destination, Level level, string sourceDirectory, bool overwrite = true)
    {
        string destinationDirectory = Path.GetDirectoryName(destination)!;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var copied = new List<string>();
        try
        {
            foreach (string path in ReferencedAssets(level, sourceDirectory).Distinct(comparer))
            {
                string relative = Path.GetRelativePath(sourceDirectory, path);
                string target = Path.GetFullPath(relative, destinationDirectory);
                if (comparer.Equals(path, target)) continue;
                if (comparer.Equals(target, destination))
                    throw new IOException($"The chart would overwrite its asset: {target}");
                if (File.Exists(target))
                {
                    if (SameFileContents(path, target)) continue;
                    throw new IOException($"The destination contains a different asset with the same name: {target}");
                }
                // A relative link outside the old chart directory can still be resolved from the
                // new directory when it points to the same file. Never create files outside it.
                if (!EditorDocument.IsLocalAsset(relative))
                    throw new IOException($"The relative asset would no longer resolve from the new directory: {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = File.OpenRead(path);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                copied.Add(target);
                input.CopyTo(output);
            }
            WriteChart(destination, level.root, overwrite);
        }
        catch
        {
            foreach (string path in copied)
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static IEnumerable<string> ReferencedAssets(Level level, string directory)
    {
        var settings = (JObject)level.root["settings"]!;
        foreach (string key in new[] { "songFilename", "bgImage", "bgVideo", "previewImage", "trackTexture" })
            if (Resolve(settings[key]) is { } path) yield return path;
        if (settings["songFilename"] is JValue { Value: string song } && !Path.IsPathRooted(song) &&
            LevelAudioLocator.Find(level, Path.Combine(directory, "level.adofai")) is { } actual)
            yield return Path.GetFullPath(actual);

        foreach (var list in new[] { level.actions, level.root["decorations"] as JArray })
        {
            if (list is null) continue;
            foreach (JObject item in list.OfType<JObject>())
            {
                JToken? image = EditorDocument.Type(item) switch
                {
                    "AddDecoration" => item["decorationImage"] ?? item["decText"],
                    "MoveDecorations" or "AddParticle" or "SetParticle" => item["decorationImage"],
                    "CustomBackground" => item["bgImage"],
                    _ => null,
                };
                if (Resolve(image) is { } path) yield return path;
                if (Resolve(item["trackTexture"]) is { } texture) yield return texture;
            }
        }

        string? Resolve(JToken? value)
        {
            if (value is not JValue { Value: string name } || string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name))
                return null;
            try
            {
                string path = Path.GetFullPath(name, directory);
                return File.Exists(path) ? path : null;
            }
            catch (ArgumentException) { return null; }
        }
    }

    private static bool SameFileContents(string first, string second)
    {
        using var original = File.OpenRead(first);
        using var existing = File.OpenRead(second);
        return original.Length == existing.Length && SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(existing));
    }

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
