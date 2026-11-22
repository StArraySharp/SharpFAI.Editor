using System.IO.Compression;
using System.IO.Hashing;

namespace SharpFAI.Editor;

internal sealed record LevelArchive(string DirectoryPath, IReadOnlyList<string> Levels)
{
    private const long MaxArchiveBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxTotalBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxPaths = 16_384;

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '|', '?', '*'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00b9", "COM\u00b2", "COM\u00b3",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"
    };

    public static bool IsZip(string path) => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    public static LevelArchive Extract(string path, long maxFileBytes = MaxFileBytes, long maxTotalBytes = MaxTotalBytes)
    {
        path = Path.GetFullPath(path);
        using FileStream file = File.OpenRead(path);
        if (file.Length > MaxArchiveBytes)
            throw new InvalidDataException("The ZIP file exceeds the 4 GiB import limit.");
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxPaths)
            throw new InvalidDataException("The ZIP contains too many entries.");

        var paths = new Dictionary<string, (string Path, bool Directory)>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(ZipArchiveEntry Entry, string Path)>();
        var levels = new List<string>();
        long total = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType is not (0 or 0x4000 or 0x8000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The ZIP contains a link or special file: {entry.FullName}");

            bool hasTrailingSlash = name.EndsWith('/');
            bool isDirectory = hasTrailingSlash || unixType == 0x4000 ||
                (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;
            if (hasTrailingSlash)
                name = name[..^1];
            string[] parts = name.Split('/');
            if (name.Length > 1024 || parts.Length > 32 || parts.Any(IsInvalidName))
                throw new InvalidDataException($"The ZIP contains an invalid path: {entry.FullName}");

            string relativePath = "";
            for (int i = 0; i < parts.Length; i++)
            {
                relativePath = Path.Join(relativePath, parts[i]);
                bool directory = i < parts.Length - 1 || isDirectory;
                if (!paths.TryAdd(relativePath, (relativePath, directory)) &&
                    (!directory || !paths[relativePath].Directory || paths[relativePath].Path != relativePath))
                    throw new InvalidDataException($"The ZIP contains conflicting paths: {entry.FullName}");
                if (paths.Count > MaxPaths)
                    throw new InvalidDataException("The ZIP contains too many files and directories.");
            }

            long length = entry.Length;
            if (length < 0 || length > maxFileBytes || length > maxTotalBytes - total || (isDirectory && length != 0))
                throw new InvalidDataException($"Invalid ZIP entry size or import limit exceeded: {entry.FullName}");
            if (isDirectory)
                continue;

            total += length;
            files.Add((entry, relativePath));
            string fileName = parts[^1];
            if (Path.GetExtension(fileName).Equals(".adofai", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Equals("backup.adofai", StringComparison.OrdinalIgnoreCase))
                levels.Add(relativePath);
        }

        if (levels.Count == 0)
            throw new InvalidDataException(L.T("NoArchiveLevels"));
        levels.Sort(StringComparer.Ordinal);

        string parent = Path.GetDirectoryName(path)!;
        string root = Path.Combine(parent, $"SharpFAI-import-{Guid.NewGuid():N}");
        if (Path.Exists(root))
            throw new IOException($"The ZIP import directory already exists: {root}");
        var output = Directory.CreateDirectory(root);
        if ((output.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"The ZIP import directory is a link: {root}");

        string rootPrefix = root + Path.DirectorySeparatorChar;
        try
        {
            foreach (var item in paths.Values)
                if (item.Directory)
                    Directory.CreateDirectory(Destination(item.Path));

            byte[] buffer = new byte[64 * 1024];
            var checksum = new Crc32();
            foreach (var (entry, relativePath) in files)
            {
                using Stream input = entry.Open();
                using FileStream destination = new(Destination(relativePath), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                checksum.Reset();
                long remaining = entry.Length;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (read > remaining)
                        throw new InvalidDataException($"The ZIP entry exceeds its declared size: {entry.FullName}");
                    checksum.Append(buffer.AsSpan(0, read));
                    destination.Write(buffer, 0, read);
                    remaining -= read;
                }
                if (remaining != 0)
                    throw new InvalidDataException($"The ZIP entry is shorter than its declared size: {entry.FullName}");
                // ZipArchive may stop at the declared size even if the compressed data has more content.
                if (checksum.GetCurrentHashAsUInt32() != entry.Crc32)
                    throw new InvalidDataException($"The ZIP entry failed its CRC-32 check: {entry.FullName}");
            }
        }
        catch
        {
            try { output.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        return new(root, levels.AsReadOnly());

        string Destination(string relativePath)
        {
            string destination = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!destination.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"ZIP entry escapes the import directory: {relativePath}");
            return destination;
        }
    }

    private static bool IsInvalidName(string name)
    {
        if (name.Length is 0 or > 255 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(InvalidNameChars) >= 0 || name.Any(char.IsControl))
            return true;
        int dot = name.IndexOf('.');
        return ReservedNames.Contains((dot < 0 ? name : name[..dot]).TrimEnd(' '));
    }
}
