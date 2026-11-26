using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpFAI.ModApi;

using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor.Mods;

internal sealed record ModPackage(ModManifest Manifest, string Root, bool Development, bool IsDropIn = false, bool IsArchive = false)
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private const int MaximumFiles = 4096;
    private static readonly Regex IdFormat = new(@"^[a-z][a-z0-9-]*(?:\.[a-z0-9-]+)+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string ApiVersion = typeof(ISharpFaiMod).Assembly.GetName().Version!.ToString(3);
    internal static ModPackage Read(string directory, bool development, string? manifestPath = null, bool isDropIn = false)
    {
        var candidates = Directory.EnumerateFiles(directory, "*.json")
            .Where(path => File.Exists(Path.ChangeExtension(path, ".dll"))).Take(2).ToArray();
        if (candidates.Length > 1) throw new InvalidDataException("Multiple DLL/JSON pairs in the Mod root.");
        string file = candidates.SingleOrDefault() ?? Path.Combine(directory, "mod.json");
        if (manifestPath is not null && !Path.GetFullPath(file).Equals(manifestPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Select the Mod's root JSON file with the matching DLL.");
        if (new FileInfo(file).Length > 128 * 1024)
            throw new InvalidDataException("The Mod manifest is too large.");
        using var input = File.OpenText(file);
        var manifest = ReadManifest(input.ReadToEnd(), candidates.Length == 1 ? Path.GetFileName(Path.ChangeExtension(file, ".dll")) : null);
        if (!File.Exists(Resolve(directory, manifest.EntryAssembly)))
            throw new InvalidDataException("The Mod entry assembly was not found.");
        return new(manifest, directory, development, IsDropIn: isDropIn);
    }

    internal static ModPackage ReadArchive(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaximumFiles)
            throw new InvalidDataException("Too many package entries.");
        var files = archive.Entries.Where(entry => entry.Name.Length != 0)
            .ToDictionary(entry => entry.FullName.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidates = files.Keys.Where(name => !name.Contains('/') && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            files.ContainsKey(Path.ChangeExtension(name, ".dll"))).Take(2).ToArray();
        if (candidates.Length > 1) throw new InvalidDataException("Multiple DLL/JSON pairs in the Mod root.");
        if (!files.TryGetValue(candidates.SingleOrDefault() ?? "mod.json", out var entry))
            throw new InvalidDataException("The Mod manifest is missing.");
        if (entry.Length > 128 * 1024)
            throw new InvalidDataException("The Mod manifest is too large.");
        using var content = new MemoryStream();
        using (var input = entry.Open()) Copy(input, content, entry.Length);
        content.Position = 0;
        using var reader = new StreamReader(content);
        var manifest = ReadManifest(reader.ReadToEnd(), candidates.Length == 1 ? Path.ChangeExtension(candidates[0], ".dll") : null);
        if (!files.ContainsKey(manifest.EntryAssembly.Replace('\\', '/')))
            throw new InvalidDataException("The Mod entry assembly was not found.");
        return new(manifest, path, Development: false, IsDropIn: true, IsArchive: true);
    }

    private static ModManifest ReadManifest(string json, string? entryAssembly)
    {
        var manifest = JsonSerializer.Deserialize<ModManifest>(json, Json)
            ?? throw new InvalidDataException("The Mod manifest is missing.");
        manifest = manifest with
        {
            ManifestVersion = manifest.ManifestVersion == 0 ? 1 : manifest.ManifestVersion,
            EntryAssembly = entryAssembly ?? manifest.EntryAssembly,
        };
        if (manifest.ManifestVersion != 1 || !ValidId(manifest.Id) || !ModVersion.TryParse(manifest.Version, out _) ||
            string.IsNullOrWhiteSpace(manifest.Name) ||
            !ModVersion.Supports(manifest.Api, ApiVersion) ||
            manifest.Dependencies?.Any(dependency => !ValidId(dependency.Id) || !ModVersion.ValidRange(dependency.Version)) == true)
            throw new InvalidDataException("Invalid or incompatible Mod manifest.");
        if (string.IsNullOrEmpty(manifest.EntryAssembly) ||
            !Resolve(string.Empty, manifest.EntryAssembly).EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Mod entry assembly was not found.");
        return manifest;
    }

    private static bool ValidId(string? id) => id is { Length: <= 120 } && IdFormat.IsMatch(id);

    internal static string Resolve(string root, string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') ||
            part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            IsDeviceName(part) ||
            part.Any(character => character < 32 || "<>\"|?*".Contains(character))))
            throw new InvalidDataException($"Invalid package path '{relative}'.");
        return Path.Combine(root, Path.Combine(parts));

        static bool IsDeviceName(string part)
        {
            string name = part.Split('.')[0].ToUpperInvariant();
            return name is "CON" or "PRN" or "AUX" or "NUL" ||
                name is { Length: 4 } && (name.StartsWith("COM") || name.StartsWith("LPT")) &&
                (name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
        }
    }

    internal static void Extract(string archivePath, string target)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumFiles)
            throw new InvalidDataException("Too many package entries.");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string relative = entry.FullName.TrimEnd('/', '\\');
            string path = Resolve(target, relative);
            if (!paths.Add(path) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (total += entry.Length) > MaximumBytes)
                throw new InvalidDataException("Invalid, duplicate or oversized package entry.");
            if (relative.Length != entry.FullName.Length)
                Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using Stream input = entry.Open();
                using Stream output = new FileStream(path, FileMode.CreateNew);
                Copy(input, output, entry.Length);
            }
        }
    }

    internal string HashAndCopy(string? target = null)
    {
        string sourceRoot = Root;
        if (IsArchive)
        {
            ArgumentNullException.ThrowIfNull(target);
            Extract(Root, target);
            sourceRoot = target;
            target = null;
        }
        var paths = new List<string>();
        Visit(new DirectoryInfo(sourceRoot), 0);
        paths.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        foreach (string file in paths)
        {
            string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            using var source = File.OpenRead(file);
            long length = source.Length;
            if ((total += length) > MaximumBytes)
                throw new InvalidDataException("The Mod exceeds the package size limit.");
            byte[] name = Encoding.UTF8.GetBytes(relative);
            hash.AppendData(Encoding.UTF8.GetBytes($"{name.Length}:{length}:"));
            hash.AppendData(name);
            using Stream? output = OpenTarget(target, relative);
            long read = 0;
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                if ((read += count) > length)
                    throw new IOException($"'{relative}' changed during activation.");
                hash.AppendData(buffer, 0, count);
                output?.Write(buffer, 0, count);
            }
            if (read != length)
                throw new IOException($"'{relative}' changed during activation.");
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Visit(DirectoryInfo directory, int depth)
        {
            if (depth > 32 || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Mod packages must use regular files and directories.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Mod packages cannot contain symbolic links.");
                if (entry is DirectoryInfo child)
                    Visit(child, depth + 1);
                else
                {
                    if (paths.Count >= MaximumFiles)
                        throw new InvalidDataException("Too many Mod files.");
                    paths.Add(entry.FullName);
                }
            }
        }
    }

    private static Stream? OpenTarget(string? root, string relative)
    {
        if (root is null) return null;
        string path = Resolve(root, relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.CreateNew);
    }

    private static void Copy(Stream input, Stream output, long limit)
    {
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if ((limit -= count) < 0)
                throw new InvalidDataException("The actual entry exceeded its declared size.");
            output.Write(buffer, 0, count);
        }
        if (limit != 0)
            throw new InvalidDataException("Incomplete package entry.");
    }
}

internal readonly record struct ModVersion(Version Number, string[] Prerelease) : IComparable<ModVersion>
{
    private static readonly Regex Format = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string? value, out ModVersion version)
    {
        version = default;
        if (value is not { Length: <= 128 }) return false;
        Match match = Format.Match(value);
        if (!match.Success || !Version.TryParse(string.Join(".", match.Groups[1], match.Groups[2], match.Groups[3]), out var number))
            return false;
        string[] labels = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (labels.Any(label => label.Length > 1 && label[0] == '0' && label.All(char.IsAsciiDigit))) return false;
        version = new(number, labels);
        return true;
    }

    public static ModVersion Parse(string value) => TryParse(value, out var result) ? result
        : throw new FormatException($"Invalid Mod version '{value}'.");

    public int CompareTo(ModVersion other)
    {
        int order = Number.CompareTo(other.Number);
        if (order != 0) return order;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return (Prerelease.Length == 0).CompareTo(other.Prerelease.Length == 0);
        for (int i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
        {
            var left = Prerelease[i];
            var right = other.Prerelease[i];
            bool leftNumeric = left.All(char.IsAsciiDigit);
            bool rightNumeric = right.All(char.IsAsciiDigit);
            order = rightNumeric.CompareTo(leftNumeric);
            if (order == 0 && leftNumeric) order = left.Length.CompareTo(right.Length);
            if (order == 0) order = StringComparer.Ordinal.Compare(left, right);
            if (order != 0) return order;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    public static bool ValidRange(string? range) => ReadRange(range, out _, out _, out _, out _);

    public static bool Supports(string? range, string version) =>
        ReadRange(range, out var min, out var max, out bool inclusiveMin, out bool inclusiveMax) &&
        TryParse(version, out var candidate) &&
        (min is null || candidate.CompareTo(min.Value) >= (inclusiveMin ? 0 : 1)) &&
        (max is null || candidate.CompareTo(max.Value) <= (inclusiveMax ? 0 : -1));

    private static bool ReadRange(string? range, out ModVersion? min, out ModVersion? max,
        out bool inclusiveMin, out bool inclusiveMax)
    {
        min = max = null;
        inclusiveMin = inclusiveMax = false;
        if (range == "*") return true;
        if (TryParse(range, out var single))
        {
            min = single;
            inclusiveMin = true;
            return true;
        }
        if (range is not { Length: >= 3 and <= 300 } || range[0] is not ('[' or '(') || range[^1] is not (']' or ')'))
            return false;
        inclusiveMin = range[0] == '[';
        inclusiveMax = range[^1] == ']';
        string[] bounds = range[1..^1].Split(',', StringSplitOptions.TrimEntries);
        if (bounds.Length == 1 && inclusiveMin && inclusiveMax && TryParse(bounds[0], out var exact))
        {
            min = max = exact;
            return true;
        }
        if (bounds.Length != 2) return false;
        if (TryParse(bounds[0], out var lower)) min = lower;
        else if (bounds[0].Length != 0 || inclusiveMin) return false;
        if (TryParse(bounds[1], out var upper)) max = upper;
        else if (bounds[1].Length != 0 || inclusiveMax) return false;
        return min is null || max is null || min.Value.CompareTo(max.Value) <= 0;
    }
}
