using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpFAI.Player;

internal static class ApplicationUpdater
{
    private const string Project = "adofaiex/SharpFAI.Editor";
    private const string ApplyArgument = "--apply-app-update";
    private const string ResultArgument = "--app-update-result";
    private const string Payload = "replacement";
    private const string Backup = "previous";
    private const string Receipt = "result.txt";
    private static readonly string Helper = OperatingSystem.IsWindows() ? "helper.exe" : "helper";
    private static readonly Version Current = new((Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)).ToString(3));
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string? Executable => Assembly.GetEntryAssembly()?.Location == "" ? Environment.ProcessPath : null;
    public static string CurrentVersion => Current.ToString(3);
    public static string? RestartError { get; private set; }

    internal sealed record Release(Version Version, Uri Download, long Size, string Sha256);
    internal sealed record Staged(Release Release, string Target, Guid Token)
    {
        public string Directory => StagePath(Target, Token);
        public void Delete() => Clean(Directory);
    }

    public static async Task<Release?> CheckAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = CreateRequest(new Uri($"https://api.github.com/repos/{Project}/releases/latest"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
        return SelectRelease(json.RootElement, Current, RuntimeInformation.RuntimeIdentifier);
    }

    internal static Release? SelectRelease(JsonElement release, Version current, string rid)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string? tag = release.GetProperty("tag_name").GetString();
        if (tag is not { Length: > 1 } || tag[0] != 'v' || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || version <= current) return null;

        string extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "";
        string name = $"SharpFAI.Editor-{rid}-{version}{extension}";
        string expected = $"https://github.com/{Project}/releases/download/{tag}/{name}";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            long size = asset.GetProperty("size").GetInt64();
            if (size > 0 && asset.GetProperty("browser_download_url").GetString() == expected &&
                asset.TryGetProperty("digest", out var value) && value.GetString() is { Length: 71 } digest &&
                digest.StartsWith("sha256:", StringComparison.Ordinal) && digest.AsSpan(7).ContainsOnlyHex())
                return new(version, new Uri(expected), size, digest[7..]);
            throw new InvalidDataException($"The GitHub asset '{name}' has invalid download metadata or no SHA-256 digest.");
        }
        throw new InvalidDataException($"The GitHub release does not contain '{name}'.");
    }

    public static async Task<Staged> DownloadAsync(Release release, Action<long> progress, CancellationToken token)
    {
        string target = Executable ?? throw new InvalidOperationException("Install updates from a published SharpFAI executable.");
        var staged = new Staged(release, target, Guid.NewGuid());
        System.IO.Directory.CreateDirectory(staged.Directory);
        try
        {
            using var request = CreateRequest(release.Download);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            string path = Path.Combine(staged.Directory, Payload);
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await CopyVerifiedAsync(source, destination, release, progress, token).ConfigureAwait(false);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(target));
            return staged;
        }
        catch
        {
            staged.Delete();
            throw;
        }
    }

    internal static async Task CopyVerifiedAsync(Stream source, Stream destination, Release release, Action<long> progress, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (count > release.Size - total) throw new InvalidDataException("The update download exceeds its declared size.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            progress(total += count);
        }
        if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update download failed SHA-256 verification.");
    }

    public static void StartInstaller(Staged staged, string[] restartArguments)
    {
        using var current = Process.GetCurrentProcess();
        string helper = Path.Combine(staged.Directory, Helper);
        File.Copy(staged.Target, helper, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, File.GetUnixFileMode(staged.Target));
        var target = new FileInfo(staged.Target);
        Start(helper, [ApplyArgument, staged.Target, staged.Token.ToString("N"),
            current.Id.ToString(CultureInfo.InvariantCulture), current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            staged.Release.Sha256, target.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), .. restartArguments]);
    }

    public static bool HandleLaunch(ref string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args is [ApplyArgument, var target, var id, var pid, var start, var sha256, var modified, .. var restart])
        {
            if (Guid.TryParseExact(id, "N", out var token) && Path.IsPathFullyQualified(target) &&
                int.TryParse(pid, CultureInfo.InvariantCulture, out int parent) &&
                long.TryParse(start, CultureInfo.InvariantCulture, out long started) &&
                long.TryParse(modified, CultureInfo.InvariantCulture, out long lastWrite) &&
                SamePath(Environment.ProcessPath, Path.Combine(StagePath(target, token), Helper)))
                exitCode = Apply(target, token, parent, started, sha256, lastWrite, restart);
            else exitCode = 2;
            return true;
        }

        if (args is [ResultArgument, var result, .. var remaining] && Guid.TryParseExact(result, "N", out var completed) &&
            Environment.ProcessPath is { } executable)
        {
            string stage = StagePath(executable, completed);
            try
            {
                string receipt = Path.Combine(stage, Receipt);
                if (File.Exists(receipt)) RestartError = File.ReadAllText(receipt);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _ = Task.Run(async () =>
            {
                for (int attempt = 0; attempt < 20 && System.IO.Directory.Exists(stage); attempt++)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    Clean(stage);
                }
            });
            args = remaining;
        }
        return false;
    }

    private static int Apply(string target, Guid token, int parent, long started, string sha256, long lastWrite, string[] args)
    {
        string stage = StagePath(target, token);
        string payload = Path.Combine(stage, Payload);
        string backup = Path.Combine(stage, Backup);
        try
        {
            Process? old;
            try { old = Process.GetProcessById(parent); }
            catch (ArgumentException) { old = null; }
            using (old)
                if (old is not null && !old.HasExited && old.StartTime.ToUniversalTime().Ticks == started && !old.WaitForExit(120_000))
                    return 1;

            if (File.GetLastWriteTimeUtc(target).Ticks != lastWrite)
                throw new IOException("The application executable changed while the update was pending.");
            using (var file = File.OpenRead(payload))
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The staged update failed SHA-256 verification.");

            File.Replace(payload, target, backup, ignoreMetadataErrors: true);
            try { Start(target, [ResultArgument, token.ToString("N"), .. args]); }
            catch
            {
                File.Move(backup, target, overwrite: true);
                throw;
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            try
            {
                if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
                File.WriteAllText(Path.Combine(stage, Receipt), error.Message);
                Start(target, [ResultArgument, token.ToString("N"), .. args]);
            }
            catch (Exception restartError) { Console.Error.WriteLine(restartError); }
            return 1;
        }
    }

    private static string StagePath(string target, Guid token) => Path.Combine(Path.GetDirectoryName(target)!,
        $".{Path.GetFileName(target)}.update-{token:N}");

    private static bool SamePath(string? first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Clean(string folder)
    {
        try
        {
            foreach (string file in new[] { Helper, Payload, Backup, Receipt }) File.Delete(Path.Combine(folder, file));
            System.IO.Directory.Delete(folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Start(string file, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException($"Could not start '{file}'.");
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd($"SharpFAI/{Current}");
        return request;
    }

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> digits)
    {
        foreach (char digit in digits)
            if (!char.IsAsciiHexDigit(digit)) return false;
        return true;
    }
}
