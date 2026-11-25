using System.IO.Compression;
using System.IO.Hashing;
using System.Text.RegularExpressions;

namespace SharpFAI.Build;

internal static partial class PreparedAssetArchive
{
    private const string Manifest = "assets.list";
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxEntries = 16_384;

    public static void Export(string workspace, string archive, CancellationToken cancellation)
    {
        archive = Path.GetFullPath(archive);
        if (File.Exists(archive))
            throw new IOException($"The resource ZIP already exists: {archive}");
        string gameFiles = Path.Combine(workspace, "GameFiles");
        string prepared = Path.Combine(gameFiles, "Prepared");
        using var preparationLock = Lock(prepared);
        string pointer = Path.Combine(gameFiles, "Prepared.current");
        string id = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : "";
        if (!Fingerprint().IsMatch(id))
            throw new InvalidDataException("No prepared game resources are selected. Prepare or import them first.");

        string source = Path.Combine(prepared, id);
        string[] files = ReadFiles(source);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        string temporary = archive + $".{Guid.NewGuid():N}.tmp";
        using var progress = new ConsoleProgressBar("Exporting prepared resources to ZIP");
        try
        {
            long total = files.Sum(file => new FileInfo(Path.Combine(source, file)).Length);
            if (total > MaxBytes || files.Length > MaxEntries)
                throw new InvalidDataException("Prepared resources exceed the ZIP size or entry limit.");
            long done = 0;
            byte[] buffer = new byte[128 * 1024];
            using (var output = new FileStream(temporary, FileMode.CreateNew))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var input = File.OpenRead(Path.Combine(source, file));
                    var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using Stream target = entry.Open();
                    int size;
                    while ((size = input.Read(buffer)) != 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        target.Write(buffer, 0, size);
                        progress.Report(done += size, total);
                    }
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, archive);
            progress.Complete($"{files.Length} files");
            Console.WriteLine($"Prepared game resources exported to: {archive}");
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void Import(string workspace, string archive, CancellationToken cancellation)
    {
        string gameFiles = Path.Combine(workspace, "GameFiles");
        string prepared = Path.Combine(gameFiles, "Prepared");
        using var preparationLock = Lock(prepared);
        using var input = File.OpenRead(archive);
        if (input.Length > MaxBytes)
            throw new InvalidDataException("The prepared resource ZIP exceeds 4 GiB.");
        string id;
        using (var progress = new ConsoleProgressBar("Fingerprinting the resource ZIP"))
        {
            byte[] hash = GameAssetPreparation.HashFileAsync(archive, done => progress.Report(done, input.Length),
                cancellation).GetAwaiter().GetResult();
            id = Convert.ToHexStringLower(hash);
            progress.Complete();
        }
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("The prepared resource ZIP contains too many entries.");
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string name = entry.FullName;
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!IsAssetPath(name) || !names.Add(name) || type is not (0 or 0x8000) ||
                (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
                entry.Length > MaxBytes - total || (name == Manifest && entry.Length > 4 * 1024 * 1024))
                throw new InvalidDataException($"Invalid entry in the prepared resource ZIP: {name}");
            total += entry.Length;
        }

        string staging = Path.Combine(prepared, $".import-{Guid.NewGuid():N}");
        string extracted = Directory.CreateDirectory(Path.Combine(staging, "assets")).FullName;
        string destination = Path.Combine(prepared, id);
        using var extract = new ConsoleProgressBar("Extracting prepared resources from ZIP");
        try
        {
            long done = 0;
            byte[] buffer = new byte[128 * 1024];
            var crc = new Crc32();
            foreach (var entry in zip.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.Combine(extracted, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream content = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew);
                crc.Reset();
                long remaining = entry.Length;
                int size;
                while ((size = content.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (size > remaining)
                        throw new InvalidDataException($"ZIP entry exceeds its declared size: {entry.FullName}");
                    crc.Append(buffer.AsSpan(0, size));
                    output.Write(buffer, 0, size);
                    remaining -= size;
                    extract.Report(done += size, total);
                }
                if (remaining != 0 || crc.GetCurrentHashAsUInt32() != entry.Crc32)
                    throw new InvalidDataException($"ZIP entry failed its size or CRC check: {entry.FullName}");
            }
            string[] files = ReadFiles(extracted);
            if (!names.SetEquals(files))
                throw new InvalidDataException("The ZIP contains files outside the prepared resource manifest.");
            extract.Complete($"{files.Length} files");

            cancellation.ThrowIfCancellationRequested();
            using var activate = new ConsoleProgressBar("Selecting the imported resources for builds");
            if (!Directory.Exists(destination))
                Directory.Move(extracted, destination);
            else
                ReadFiles(destination);
            string next = Path.Combine(staging, "Prepared.current");
            File.WriteAllText(next, id);
            File.Move(next, Path.Combine(gameFiles, "Prepared.current"), overwrite: true);
            activate.Complete();
            Console.WriteLine($"Prepared game resources imported to: {destination}");
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private static FileStream Lock(string prepared)
    {
        Directory.CreateDirectory(prepared);
        return GameAssetPreparation.OpenLock(Path.Combine(prepared, ".prepare.lock"),
            "Another game asset preparation is already running.");
    }

    private static string[] ReadFiles(string directory)
    {
        string manifest = Path.Combine(directory, Manifest);
        string managed = Path.Combine(directory, "Managed");
        if (!File.Exists(manifest) || !File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")) ||
            !File.Exists(Path.Combine(managed, "Assembly-CSharp-firstpass.dll")))
            throw new InvalidDataException("Prepared game resources require assets.list and both Assembly-CSharp DLLs.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Manifest };
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var textures = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new List<string>();
        foreach (string line in File.ReadLines(manifest))
        {
            if (line.Split('|') is not [string kind, string key, string path] || !keys.Add(key))
                throw new InvalidDataException($"Invalid prepared resource record: {line}");
            if (kind == "T" && key.StartsWith("Resources.Filters.", StringComparison.Ordinal) &&
                key.EndsWith("_0.png", StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(path);
                continue;
            }
            if (kind != "R" || ResourceKey(path) != key || !IsAssetPath(path) ||
                !files.Add(path) || !File.Exists(Path.Combine(directory, path)))
                throw new InvalidDataException($"Invalid or missing prepared resource: {line}");
            if (path.StartsWith("Textures/", StringComparison.Ordinal)) textures.Add(key);
        }
        if (files.Count <= 1 || aliases.Any(alias => !textures.Contains(alias)))
            throw new InvalidDataException("The prepared resource manifest is empty or has an invalid texture alias.");

        foreach (string dll in Directory.EnumerateFiles(managed, "*.dll"))
        {
            string name = "Managed/" + Path.GetFileName(dll);
            if (!IsAssetPath(name) || !files.Add(name))
                throw new InvalidDataException($"Invalid prepared assembly name: {name}");
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static string? ResourceKey(string path)
    {
        if (path.StartsWith("Textures/", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.Ordinal))
            return "Resources.Filters." + path["Textures/".Length..];
        if (path.StartsWith("Metadata/Textures/", StringComparison.Ordinal) && path.EndsWith(".png.meta", StringComparison.Ordinal))
            return "Resources.Filters." + path["Metadata/Textures/".Length..];
        if (path.StartsWith("Shaders/", StringComparison.Ordinal) && path.EndsWith(".glsl", StringComparison.Ordinal))
            return "Resources.Shaders.Filters/" + path["Shaders/".Length..];
        if (path.StartsWith("Metadata/Shaders/", StringComparison.Ordinal) && path.EndsWith(".shaderlab", StringComparison.Ordinal))
            return "Resources.Shaders.Filters/" + path["Metadata/Shaders/".Length..];
        return null;
    }

    private static bool IsAssetPath(string path)
    {
        if (path == Manifest) return true;
        if (path.Length > 512 || path.Split('/').Any(part => !SafeName().IsMatch(part) ||
            part[^1] is '.' or ' ' || ReservedName().IsMatch(part)))
            return false;
        return ResourceKey(path) is not null ||
            path.StartsWith("Managed/", StringComparison.Ordinal) &&
            path.Count(character => character == '/') == 1 && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex Fingerprint();

    [GeneratedRegex(@"\A[A-Za-z0-9_ .-]{1,255}\z")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]) *(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedName();
}
