using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpFAI.Build;

internal sealed record CachedTool(string Executable, string Marker, string Tag);

internal static class ToolCache
{
    private const string MarkerName = ".archive-sha256";

    public static async Task<(CachedTool Ripper, CachedTool Hlslcc)> PrepareAsync(
        string workspace, bool checkLatest, CancellationToken cancellation)
    {
        string directory = Directory.CreateDirectory(Path.Combine(workspace, "tools")).FullName;
        using var cacheLock = GameAssetPreparation.OpenLock(Path.Combine(directory, ".download.lock"),
            "Another game tool download is already running.");
        var ripper = await FindOrDownloadAsync(directory, "AssetRipperPatcher", "AssetRipperPatcher.exe",
            new Version(1, 0, 3), checkLatest, cancellation);
        var hlslcc = await FindOrDownloadAsync(directory, "HLSLcc", "hlslcc.exe",
            new Version(1, 0, 0), checkLatest, cancellation);
        return (ripper, hlslcc);
    }

    private static async Task<CachedTool> FindOrDownloadAsync(string directory, string name, string executable,
        Version minimum, bool checkLatest, CancellationToken cancellation)
    {
        string root = Path.Combine(directory, name);
        if (!checkLatest)
        {
            using var check = new ConsoleProgressBar($"Checking the {name} cache");
            var versions = Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];
            var cached = versions
                .Select(directory => (Directory: directory, Version: ParseVersion(Path.GetFileName(directory))))
                .Where(candidate => candidate.Version >= minimum)
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => ReadCache(candidate.Directory, executable))
                .FirstOrDefault(candidate => candidate != null);
            check.Complete(cached is null ? "download required" : $"using {cached.Tag}");
            if (cached != null)
                return cached;
        }

        string repository = $"StArraySharp/{name}";
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SharpFAI-GameAssets");
        Release release;
        using (var check = new ConsoleProgressBar($"Checking the latest {name} release"))
        {
            release = await client.GetFromJsonAsync<Release>($"https://api.github.com/repos/{repository}/releases/latest", cancellation)
                ?? throw new IOException($"GitHub returned an empty release for {name}.");
            if (ParseVersion(release.Tag) is not { } version || version < minimum)
                throw new IOException($"Expected a stable {name} release at version {minimum} or newer.");
            check.Complete(release.Tag);
        }

        var assets = release.Assets.Where(asset => Regex.IsMatch(asset.Name, @"-(?:windows|win)-x64\.zip$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        if (assets.Length != 1)
            throw new IOException($"Expected one Windows x64 ZIP for {name} {release.Tag}, found {assets.Length}.");
        var package = assets[0];
        string digest = package.Digest ?? "";
        string prefix = $"https://github.com/{repository}/releases/download/{release.Tag}/";
        if (!Regex.IsMatch(digest, @"\Asha256:[a-fA-F0-9]{64}\z") || !package.Url.StartsWith(prefix, StringComparison.Ordinal))
            throw new IOException($"Invalid release URL or SHA-256 digest for {package.Name}.");

        string expectedHash = digest["sha256:".Length..];
        string destination = Path.Combine(root, release.Tag);
        if (Directory.Exists(destination))
        {
            using var check = new ConsoleProgressBar($"Checking cached {name} {release.Tag}");
            var cached = ReadCache(destination, executable, expectedHash)
                ?? throw new IOException($"Tool cache does not match the release. Move it aside and try again: {destination}");
            check.Complete("already installed");
            return cached;
        }

        Directory.CreateDirectory(root);
        string staging = Path.Combine(root, $".download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            string archive = Path.Combine(staging, "release.zip");
            using (var progress = new ConsoleProgressBar($"Downloading {name} {release.Tag}"))
            {
                using var response = await client.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? package.Size;
                await using var download = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = File.Create(archive);
                byte[] buffer = new byte[64 * 1024];
                long downloaded = 0;
                int count;
                while ((count = await download.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    downloaded += count;
                    progress.Report(downloaded, total);
                }
                progress.Complete($"{downloaded:N0} bytes");
            }

            using (var progress = new ConsoleProgressBar($"Verifying {name} SHA-256"))
            {
                long length = new FileInfo(archive).Length;
                string actual = Convert.ToHexString(await GameAssetPreparation.HashFileAsync(archive,
                    bytes => progress.Report(bytes, length), cancellation));
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"SHA-256 mismatch for {package.Name}.");
                progress.Complete();
            }

            using var extract = new ConsoleProgressBar($"Extracting and installing {name} {release.Tag}");
            cancellation.ThrowIfCancellationRequested();
            string extracted = Path.Combine(staging, "extracted");
            ZipFile.ExtractToDirectory(archive, extracted);
            cancellation.ThrowIfCancellationRequested();
            string[] executables = Directory.GetFiles(extracted, executable, SearchOption.AllDirectories);
            if (executables is not [string path])
                throw new IOException($"Expected one {executable} in {package.Name}, found {executables.Length}.");
            string relative = Path.GetRelativePath(extracted, path);
            File.WriteAllText(Path.Combine(extracted, MarkerName), expectedHash);
            Directory.Move(extracted, destination);
            extract.Complete();
            return new CachedTool(Path.Combine(destination, relative), Path.Combine(destination, MarkerName), release.Tag);
        }
        finally
        {
            using var cleanup = new ConsoleProgressBar($"Removing temporary {name} download files");
            Directory.Delete(staging, recursive: true);
            cleanup.Complete();
        }
    }

    private static Version? ParseVersion(string tag) =>
        Regex.IsMatch(tag, @"\Av?[0-9]+\.[0-9]+\.[0-9]+\z") && Version.TryParse(tag.TrimStart('v'), out var version)
            ? version
            : null;

    private static CachedTool? ReadCache(string directory, string executable, string? expectedHash = null)
    {
        string marker = Path.Combine(directory, MarkerName);
        if (!File.Exists(marker))
            return null;
        string hash = File.ReadAllText(marker).Trim();
        if (!Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z") ||
            (expectedHash is not null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)))
            return null;
        string? path = Directory.EnumerateFiles(directory, executable, SearchOption.AllDirectories).FirstOrDefault();
        return path is null ? null : new CachedTool(path, marker, Path.GetFileName(directory));
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag, ReleaseAsset[] Assets);
    private sealed record ReleaseAsset(string Name, [property: JsonPropertyName("browser_download_url")] string Url,
        string? Digest, long Size);
}
