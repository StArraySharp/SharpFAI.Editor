using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SharpFAI.Build;

internal sealed class AssetCompiler(string output, CachedTool hlslcc)
{
    private static readonly string[] RequiredTextures =
    [
        "CameraFilterPack_VHS1.png", "CameraFilterPack_VHS2.png", "MotionBlurJitter.png",
        "Rotating_Petals_1.png", "Rotating_Petals_2.png", "Rotating_Petals_3.png", "tutorial_tile.png",
    ];
    private static readonly Regex ShaderDeclaration = new(@"(?m)^[ \t]*Shader\s+""([^""]+)""");

    private readonly List<string> manifest = [];
    private readonly Dictionary<string, string> stages = new(StringComparer.OrdinalIgnoreCase);

    public async Task CompileAsync(string exported, ShaderLayout[] shaders, string workingDirectory, CancellationToken cancellation,
        string? connectorTexture)
    {
        CopyTextures(Path.Combine(exported, "Texture2D"), connectorTexture, cancellation);
        var sourceFiles = ReadShaderLabExports(exported, cancellation);
        var sourceNames = shaders.Select(shader => shader.Name).ToHashSet(StringComparer.Ordinal);
        using var compile = new ConsoleProgressBar($"Translating shaders to GLSL with HLSLcc {hlslcc.Tag}");
        int passCount = 0;
        int shaderCount = 0;
        for (int shaderIndex = 0; shaderIndex < shaders.Length; shaderIndex++)
        {
            cancellation.ThrowIfCancellationRequested();
            compile.Report(shaderIndex, shaders.Length);
            var shader = shaders[shaderIndex];
            string resourceName = shader.Name switch
            {
                "CameraFilterPack/Distortion_float_Sphere" => "CameraFilterPack/Distortion_Half_Sphere",
                "CameraFilterPack/Drawing_floattone" => "CameraFilterPack/Drawing_Halftone",
                _ => shader.Name,
            };
            if (resourceName != shader.Name && sourceNames.Contains(resourceName))
                continue;
            if (!Regex.IsMatch(shader.Name, @"\A[A-Za-z0-9_ .-]+(/[A-Za-z0-9_ .-]+)+\z") ||
                shader.Name.Split('/').Any(part => part.TrimEnd(' ', '.').Length == 0) ||
                !sourceFiles.TryGetValue(shader.Name, out string? asset))
                throw new IOException($"Missing or invalid ShaderLab metadata for {shader.Name}.");
            if (shader.SubShaders is not [var passes, ..])
                throw new IOException($"No subshaders were found for {shader.Name}.");

            string rawPrograms = Path.Combine(Path.GetDirectoryName(asset)!,
                Path.GetFileNameWithoutExtension(asset) + "_asm", "d3d11");
            for (int index = 0; index < passes.Length; index++)
            {
                var pass = passes[index];
                if (pass.Type != 0)
                    continue;
                string vertex = await TranslateStageAsync(rawPrograms, pass.Vertex, "vs", workingDirectory, cancellation);
                string fragment = await TranslateStageAsync(rawPrograms, pass.Fragment, "ps", workingDirectory, cancellation);
                string name = index == 0 ? resourceName : $"{resourceName}.pass-{index}";
                AddAsset($"Resources.Shaders.Filters/{name}.glsl", $"Shaders/{name}.glsl", path =>
                    File.WriteAllText(path, $"// SharpFAI HLSLcc DXBC\n#ifdef VERTEX\n{vertex}\n#endif\n#ifdef FRAGMENT\n{fragment}\n#endif\n"));
                passCount++;
            }

            string properties = File.ReadAllText(asset);
            Match body = Regex.Match(properties, @"(?m)^\s*SubShader\b");
            if (body.Success)
                properties = properties[..body.Index] + "\n}\n";
            if (resourceName != shader.Name)
                properties = ShaderDeclaration.Replace(properties, $"Shader \"{resourceName}\"", 1);
            AddAsset($"Resources.Shaders.Filters/{resourceName}.shaderlab", $"Metadata/Shaders/{resourceName}.shaderlab",
                path => File.WriteAllText(path, properties));
            shaderCount++;
        }

        if (passCount == 0)
            throw new IOException("No shader passes were prepared.");
        compile.Complete($"{passCount} GLSL passes from {shaderCount} shaders");

        using var writeManifest = new ConsoleProgressBar("Writing the prepared resource manifest");
        File.WriteAllLines(Path.Combine(output, "assets.list"), manifest);
        writeManifest.Complete($"{manifest.Count} entries");
    }

    private void CopyTextures(string directory, string? connectorTexture, CancellationToken cancellation)
    {
        using var progress = new ConsoleProgressBar("Copying textures and checking duplicates");
        var selected = Directory.GetFiles(directory, "*.png")
            .Where(path => Path.GetFileName(path).StartsWith("CameraFilterPack", StringComparison.Ordinal) ||
                RequiredTextures.Contains(Path.GetFileName(path), StringComparer.Ordinal))
            .ToDictionary(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
        if (connectorTexture is not null)
            selected.TryAdd("dotted-line.png", connectorTexture);
        foreach (string required in RequiredTextures)
        {
            if (!selected.ContainsKey(required))
                throw new IOException($"Required texture {required} was not extracted.");
        }

        int count = 0;
        int index = 0;
        foreach (var (name, texture) in selected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            progress.Report(index++, selected.Count);
            if (!Regex.IsMatch(name, @"\A[A-Za-z0-9_. -]+\.png\z"))
                throw new IOException($"Invalid texture name: {name}");
            string key = $"Resources.Filters.{name}";
            if (Path.GetFileNameWithoutExtension(name).EndsWith("_0", StringComparison.OrdinalIgnoreCase))
            {
                string canonical = name[..^"_0.png".Length] + ".png";
                if (!selected.TryGetValue(canonical, out string? original) ||
                    !TextureHash(original).SequenceEqual(TextureHash(texture)))
                    continue;
                manifest.Add($"T|{key}|Resources.Filters.{Path.GetFileName(original)}");
            }
            else
            {
                AddAsset(key, $"Textures/{name}", path => File.Copy(texture, path));
                count++;
            }

            if (File.Exists(texture + ".meta"))
                AddAsset(key + ".meta", $"Metadata/Textures/{name}.meta", path => File.Copy(texture + ".meta", path));
        }
        progress.Complete($"{count} textures");
    }

    private static byte[] TextureHash(string path)
    {
        using var texture = File.OpenRead(path);
        return SHA256.HashData(texture);
    }

    private static Dictionary<string, string> ReadShaderLabExports(string exported, CancellationToken cancellation)
    {
        using var progress = new ConsoleProgressBar("Indexing exported ShaderLab files");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] files = Directory.GetFiles(exported, "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress.Report(i, files.Length);
            string path = files[i];
            using var shader = File.OpenText(path);
            string? line;
            while ((line = shader.ReadLine()) != null)
            {
                Match match = ShaderDeclaration.Match(line);
                if (!match.Success)
                    continue;
                string name = match.Groups[1].Value;
                if (Path.GetFileNameWithoutExtension(path).Equals(name.Replace('/', '_'), StringComparison.Ordinal))
                    result[name] = path;
                else
                    result.TryAdd(name, path);
                break;
            }
        }
        progress.Complete($"{files.Length} files");
        return result;
    }

    private async Task<string> TranslateStageAsync(string directory, uint[] indices, string stage, string workingDirectory,
        CancellationToken cancellation)
    {
        var candidates = indices.Select(index => Path.Combine(directory, $"{index}_{stage}.dxbc"))
            .Where(File.Exists).ToArray();
        if (candidates is not [string file])
            throw new IOException($"Expected one default {stage} program in {directory}, found {candidates.Length}.");
        if (stages.TryGetValue(file, out string? cached))
            return cached;

        // HLSLcc cannot open some deeply nested AssetRipper exports on Windows.
        const string translatorInput = "translate.dxbc";
        File.Copy(file, Path.Combine(workingDirectory, translatorInput), overwrite: true);
        string source = await GameAssetPreparation.RunToolAsync(hlslcc.Executable, ["-q", "-l", "glsl330",
            "--no-default-flags", "--flag", "disable-globals-struct", "--strip-unused", translatorInput],
            workingDirectory, cancellation);
        Match version = Regex.Match(source, @"(?m)^#version 330\b");
        if (!version.Success)
            throw new IOException($"HLSLcc did not return GLSL 330 for {file}.");
        source = source[version.Index..].ReplaceLineEndings("\n").TrimEnd();
        if (Regex.IsMatch(source, @"(?m)^\s*uniform\s+\w*sampler\w*\s+_tex\d+\b"))
            throw new IOException($"Unresolved texture binding in {file}.");

        const string matrices = "unity_ObjectToWorld|unity_MatrixVP|_ToPrevViewProjCombined";
        source = Regex.Replace(source, $@"\b({matrices})(?=\s*\[)", "hlslcc_mtx4x4$1");
        source = Regex.Replace(source, $@"\buniform\s+mat4x4\s+({matrices})\s*;", "uniform vec4 hlslcc_mtx4x4$1[4];");
        stages.Add(file, source);
        return source;
    }

    private void AddAsset(string key, string relative, Action<string> write)
    {
        string path = Path.Combine(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        write(path);
        manifest.Add($"R|{key}|{relative}");
    }
}
