using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpFAI.Build;

internal static class GameAssetPreparation
{
    private static readonly Regex ExportProgress = new(@"\bExportProgress\s*:\s*\((\d+)\s*/\s*(\d+)\)");

    public static async Task RunAsync(string workspace, string gameDirectory, CancellationToken cancellation)
    {
        string data;
        using (var validate = new ConsoleProgressBar("Validating the ADOFAI installation"))
        {
            data = FindGameData(gameDirectory);
            validate.Complete();
        }
        string assets = Path.Combine(data, "resources.assets");
        string? connectorAssets;
        using (var locate = new ConsoleProgressBar("Locating the editor's track connector texture"))
        {
            connectorAssets = FindConnectorAssets(data, cancellation);
            locate.Complete(connectorAssets is null ? "texture unavailable" : Path.GetFileName(connectorAssets));
        }
        string managed = Path.Combine(data, "Managed");
        string[] assemblies = Directory.GetFiles(managed, "*.dll");
        string gameFiles = Path.Combine(workspace, "GameFiles");
        string prepared = Path.Combine(gameFiles, "Prepared");
        string pointer = Path.Combine(gameFiles, "Prepared.current");
        Directory.CreateDirectory(prepared);
        using var preparationLock = OpenLock(Path.Combine(prepared, ".prepare.lock"),
            "Another game asset preparation is already running.");

        var (ripper, hlslcc) = await ToolCache.PrepareAsync(workspace, checkLatest: false, cancellation);
        string toolPath = typeof(GameAssetPreparation).Assembly.Location;
        if (toolPath.Length == 0)
            toolPath = Environment.ProcessPath ?? throw new IOException("Cannot identify the resource preparation executable.");
        var inputs = new[] { toolPath, assets, ripper.Marker, hlslcc.Marker }
            .Concat(Directory.EnumerateFiles(data, "resources.assets.*"))
            .Concat(assemblies)
            .Concat(ToolBinaries(ripper))
            .Concat(ToolBinaries(hlslcc));
        if (connectorAssets is not null)
            inputs = inputs.Append(connectorAssets).Concat(Directory.EnumerateFiles(data, Path.GetFileName(connectorAssets) + ".*"));
        string fingerprint = await FingerprintAsync(inputs, cancellation);
        string destination = Path.Combine(prepared, fingerprint);
        string staging = Path.Combine(prepared, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            bool cached;
            using (var check = new ConsoleProgressBar("Checking prepared resources"))
            {
                cached = HasPreparedAssets(destination, assemblies);
                check.Complete(cached ? "using cached game assets" : "preparation required");
            }

            if (!cached)
            {
                string rip = Path.Combine(staging, "Rip");
                using (var extract = new ConsoleProgressBar($"Extracting game resources with AssetRipperPatcher {ripper.Tag}"))
                {
                    await RunToolAsync(ripper.Executable, [assets, rip, "--only", "Texture2D,Shader",
                        "--only-strict", "--shader-asm"], staging, cancellation, line =>
                    {
                        Match match = ExportProgress.Match(line);
                        if (match.Success && long.TryParse(match.Groups[1].ValueSpan, out long completed) &&
                            long.TryParse(match.Groups[2].ValueSpan, out long total))
                            extract.Report(completed, total);
                    });
                    extract.Complete();
                }
                ShaderLayout[] layout;
                using (var readLayout = new ConsoleProgressBar("Reading Unity shader pass data"))
                {
                    layout = ShaderLayoutReader.Read(ripper, assets, readLayout, cancellation);
                    readLayout.Complete($"{layout.Length} shaders");
                }
                string? connectorTexture = null;
                if (connectorAssets is not null)
                {
                    using var extract = new ConsoleProgressBar("Extracting the track connector texture");
                    string editorRip = Path.Combine(staging, "EditorTextures");
                    await RunToolAsync(ripper.Executable, [connectorAssets, editorRip, "--only", "Texture2D",
                        "--only-strict"], staging, cancellation, line =>
                    {
                        Match match = ExportProgress.Match(line);
                        if (match.Success && long.TryParse(match.Groups[1].ValueSpan, out long completed) &&
                            long.TryParse(match.Groups[2].ValueSpan, out long total))
                            extract.Report(completed, total);
                    });
                    connectorTexture = Path.Combine(editorRip, "ExportedProject", "Assets", "Texture2D", "dotted-line.png");
                    if (!File.Exists(connectorTexture))
                        throw new IOException("The editor's dotted-line texture was not exported.");
                    extract.Complete();
                }

                if (Directory.Exists(destination))
                    Directory.Move(destination, Path.Combine(staging, "Incomplete"));
                Directory.CreateDirectory(destination);
                try
                {
                    using (var copy = new ConsoleProgressBar("Copying game assemblies for the build"))
                    {
                        string preparedManaged = Directory.CreateDirectory(Path.Combine(destination, "Managed")).FullName;
                        for (int i = 0; i < assemblies.Length; i++)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            copy.Report(i, assemblies.Length);
                            File.Copy(assemblies[i], Path.Combine(preparedManaged, Path.GetFileName(assemblies[i])));
                        }
                        copy.Complete($"{assemblies.Length} assemblies");
                    }
                    string exported = Path.Combine(rip, "ExportedProject", "Assets");
                    await new AssetCompiler(destination, hlslcc).CompileAsync(exported, layout, staging, cancellation,
                        connectorTexture);
                }
                catch
                {
                    Directory.Delete(destination, recursive: true);
                    throw;
                }
            }

            cancellation.ThrowIfCancellationRequested();
            using var publish = new ConsoleProgressBar("Selecting the prepared resources for builds");
            string next = Path.Combine(staging, "Prepared.current");
            File.WriteAllText(next, fingerprint);
            File.Move(next, pointer, overwrite: true);
            publish.Complete();
        }
        finally
        {
            using var cleanup = new ConsoleProgressBar("Removing temporary resource files");
            Directory.Delete(staging, recursive: true);
            cleanup.Complete();
        }
        Console.WriteLine($"Game assets are ready for offline builds: {destination}");
    }

    private static string? FindConnectorAssets(string data, CancellationToken cancellation)
    {
        // Unity serializes the texture name with its byte length. Match the complete name,
        // including its prefix, so alternate textures such as dotted-line-3 are skipped.
        ReadOnlySpan<byte> name = "\u000b\0\0\0dotted-line"u8;
        byte[] buffer = new byte[128 * 1024];
        foreach (string path in Directory.EnumerateFiles(data, "sharedassets*.assets").Order(StringComparer.Ordinal))
        {
            using var file = File.OpenRead(path);
            int carry = 0;
            int read;
            while ((read = file.Read(buffer.AsSpan(carry))) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                Span<byte> chunk = buffer.AsSpan(0, carry + read);
                if (chunk.IndexOf(name) >= 0)
                    return path;
                carry = Math.Min(chunk.Length, name.Length - 1);
                chunk[^carry..].CopyTo(buffer);
            }
        }
        return null;
    }

    internal static FileStream OpenLock(string lockPath, string busyMessage)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 32)
        {
            throw new IOException(busyMessage, ex);
        }
    }

    private static string FindGameData(string gameDirectory)
    {
        var selected = new DirectoryInfo(gameDirectory);
        if (!selected.Exists)
            throw new DirectoryNotFoundException($"Game directory does not exist: {selected.FullName}");
        var candidates = selected.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
            ? [selected]
            : selected.GetDirectories("*_Data");
        var matches = candidates.Select(candidate => candidate.FullName).Where(directory =>
            File.Exists(Path.Combine(directory, "resources.assets")) &&
            File.Exists(Path.Combine(directory, "Managed", "Assembly-CSharp.dll")) &&
            File.Exists(Path.Combine(directory, "Managed", "Assembly-CSharp-firstpass.dll"))).ToArray();
        if (matches.Length != 1 || !IsAdofai(matches[0]))
            throw new IOException("Select one ADOFAI installation or _Data folder with resources.assets and both Assembly-CSharp DLLs.");
        return matches[0];
    }

    private static bool IsAdofai(string data)
    {
        string managed = Path.Combine(data, "Managed");
        try
        {
            using var stream = File.OpenRead(Path.Combine(managed, "Assembly-CSharp.dll"));
            using var executable = new PEReader(stream);
            var metadata = executable.GetMetadataReader();
            return metadata.GetString(metadata.GetAssemblyDefinition().Name) == "Assembly-CSharp" &&
                metadata.TypeDefinitions.Any(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "scrController") &&
                AssemblyName.GetAssemblyName(Path.Combine(managed, "Assembly-CSharp-firstpass.dll")).Name == "Assembly-CSharp-firstpass";
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static IEnumerable<string> ToolBinaries(CachedTool tool) =>
        Directory.EnumerateFiles(Path.GetDirectoryName(tool.Executable)!)
            .Where(path => Path.GetExtension(path) is ".dll" or ".exe" || path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal));

    private static async Task<string> FingerprintAsync(IEnumerable<string> inputs, CancellationToken cancellation)
    {
        using var progress = new ConsoleProgressBar("Fingerprinting the game and tools");
        var files = inputs.Order(StringComparer.Ordinal).Select(path => new FileInfo(path)).ToArray();
        long total = files.Sum(file => file.Length);
        long completed = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            fingerprint.AppendData(Encoding.UTF8.GetBytes(file.Name + "\0"));
            fingerprint.AppendData(await HashFileAsync(file.FullName, bytes => progress.Report(completed + bytes, total), cancellation));
            completed += file.Length;
        }
        progress.Complete($"{files.Length} files");
        return Convert.ToHexStringLower(fingerprint.GetHashAndReset());
    }

    internal static async Task<byte[]> HashFileAsync(string path, Action<long> report, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long completed = 0;
            int count;
            while ((count = await file.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                completed += count;
                report(completed);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasPreparedAssets(string directory, string[] assemblies)
    {
        string list = Path.Combine(directory, "assets.list");
        if (!File.Exists(list) ||
            assemblies.Any(assembly => !File.Exists(Path.Combine(directory, "Managed", Path.GetFileName(assembly)))))
            return false;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(list))
        {
            if (line.Split('|') is not [string kind, string name, string value] || !keys.Add(name) ||
                kind is not ("R" or "T") || (kind == "R" && !File.Exists(Path.Combine(directory, value))))
                return false;
        }
        return keys.Count != 0;
    }

    internal static async Task<string> RunToolAsync(string executable, string[] args, string directory,
        CancellationToken cancellation, Action<string>? onOutput = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {executable}.");
        Task<string> stdout = ReadOutputAsync(process.StandardOutput, onOutput);
        Task<string> stderr = ReadOutputAsync(process.StandardError, onOutput);
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        string output = await stdout;
        string errors = await stderr;
        if (process.ExitCode != 0)
        {
            string tail = string.Join(Environment.NewLine, (output + Environment.NewLine + errors).Trim().Split('\n').TakeLast(16));
            throw new IOException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}.{Environment.NewLine}{tail}");
        }
        return output;
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, Action<string>? onOutput)
    {
        if (onOutput is null)
            return await reader.ReadToEndAsync();
        var output = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            output.AppendLine(line);
            onOutput(line);
        }
        return output.ToString();
    }
}
