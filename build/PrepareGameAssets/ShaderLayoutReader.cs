using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

namespace SharpFAI.Build;

internal sealed record ShaderLayout(string Name, ShaderPass[][]? SubShaders);
internal sealed record ShaderPass(int Type, uint[] Vertex, uint[] Fragment);

internal static class ShaderLayoutReader
{
    public static ShaderLayout[] Read(CachedTool ripper, string assets, ConsoleProgressBar progress, CancellationToken cancellation)
    {
        // The CLI can build before AssetRipper is installed. Only the pass reader
        // binds to the downloaded libraries, with Unity objects confined to this method.
        var libraries = new AssetRipperLibraries(Path.GetDirectoryName(ripper.Executable)!);
        try
        {
            return ReadLayouts(libraries, assets, progress, cancellation);
        }
        catch (Exception ex) when (ex is MemberAccessException or TypeLoadException or TargetInvocationException or IOException)
        {
            throw new IOException($"Cannot read Unity shader pass data with AssetRipperPatcher {ripper.Tag}: " +
                (ex.InnerException ?? ex).Message, ex);
        }
        finally
        {
            libraries.Unload();
        }
    }

    private static ShaderLayout[] ReadLayouts(AssetRipperLibraries libraries, string assets,
        ConsoleProgressBar progress, CancellationToken cancellation)
    {
        Type gameType = libraries.GetType("Import", "Import.Structure.GameStructure");
        Type configType = libraries.GetType("Import", "Import.Configuration.CoreConfiguration");
        Type fileSystem = libraries.GetType("IO.Files", "IO.Files.LocalFileSystem");
        Type shaderType = libraries.GetType("SourceGenerated", "SourceGenerated.Classes.ClassID_48.IShader");
        Type platformType = libraries.GetType("SourceGenerated.Extensions", "SourceGenerated.Extensions.Enums.Shader.GPUPlatform");
        uint d3d11 = Convert.ToUInt32(Enum.Parse(platformType, "D3D11"));
        object config = Activator.CreateInstance(configType)!;
        object settings = Property(config, "ImportSettings")!;
        var content = FindProperty(settings.GetType(), "ScriptContentLevel");
        content.SetValue(settings, Enum.Parse(content.PropertyType, "Level0"));
        object local = FindProperty(fileSystem, "Instance").GetValue(null)!;
        var load = FindMethod(gameType, "Load");
        using var game = (IDisposable)load.Invoke(null, [new[] { assets }, local, config])!;
        object bundle = Property(game, "FileCollection")!;
        var objects = (IEnumerable)FindMethod(bundle.GetType(), "FetchAssets").Invoke(bundle, null)!;
        var shaders = objects.Cast<object>().Where(shaderType.IsInstanceOfType).ToArray();
        var layouts = new SortedDictionary<string, ShaderLayout>(StringComparer.Ordinal);
        for (int i = 0; i < shaders.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress.Report(i, shaders.Length);
            object asset = shaders[i];
            if (Property(asset, "Platforms") is not IEnumerable<uint> platforms || !platforms.Contains(d3d11))
                continue;
            string name = (string)Property(Property(asset, "Name")!, "String")!;
            if (!IsFilter(name))
                continue;

            var parsed = Property(asset, "ParsedForm");
            var subShaders = parsed is null ? null : Items(parsed, "SubShaders").Select(subShader =>
                Items(subShader, "Passes").Select(pass => new ShaderPass(
                    Convert.ToInt32(Property(pass, "Type")),
                    DefaultPrograms(Property(pass, "ProgVertex")!),
                    DefaultPrograms(Property(pass, "ProgFragment")!))).ToArray()).ToArray();
            var layout = new ShaderLayout(name, subShaders);
            if (!layouts.TryAdd(name, layout) && !SameLayout(layouts[name].SubShaders, subShaders))
                throw new IOException($"Conflicting pass tables for shader {name}.");
        }
        return layouts.Values.ToArray();
    }

    private static bool IsFilter(string name) =>
        name.StartsWith("CameraFilterPack/", StringComparison.Ordinal) ||
        name.StartsWith("CameraFilterPackLegacy/", StringComparison.Ordinal) ||
        name is "ADOFAI/PolarScreen" or "ADOFAI/ScreenTile" or "ADOFAI/ScreenScroll" or "ADOFAI/ScrollingSprite" or
            "Sprites/Tile" or "Hidden/VideoBloom" or "Hidden/CameraMotionBlur";

    private static uint[] DefaultPrograms(object program)
    {
        bool hasPlayerPrograms = (bool)FindMethod(program.GetType(), "Has_PlayerSubPrograms").Invoke(program, null)!;
        if (hasPlayerPrograms)
        {
            var playerPrograms = Items(program, "PlayerSubPrograms")
                .SelectMany(group => ((IEnumerable)group).Cast<object>()).ToArray();
            if (playerPrograms.Length != 0)
                return DefaultBlobIndices(playerPrograms);
        }
        return DefaultBlobIndices(Items(program, "SubPrograms"));
    }

    private static uint[] DefaultBlobIndices(IEnumerable<object> programs) => programs
        .Where(program => Property(program, "KeywordIndices") is not IEnumerable keywords || !keywords.Cast<object>().Any())
        .Select(program => (uint)Property(program, "BlobIndex")!).Distinct().ToArray();

    private static bool SameLayout(ShaderPass[][]? left, ShaderPass[][]? right)
    {
        if (left is null || right is null)
            return left == right;
        return left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Length == pair.Second.Length && pair.First.Zip(pair.Second).All(passes =>
                passes.First.Type == passes.Second.Type &&
                passes.First.Vertex.AsSpan().SequenceEqual(passes.Second.Vertex) &&
                passes.First.Fragment.AsSpan().SequenceEqual(passes.Second.Fragment)));
    }

    private static IEnumerable<object> Items(object target, string name) =>
        ((IEnumerable)Property(target, name)!).Cast<object>();

    private static object? Property(object target, string name) => FindProperty(target.GetType(), name).GetValue(target);

    // Unity's generated asset types implement some members explicitly on version-specific interfaces.
    private static PropertyInfo FindProperty(Type type, string name) =>
        type.GetProperty(name) ?? type.GetInterfaces().Select(contract => contract.GetProperty(name))
            .FirstOrDefault(property => property is not null) ?? throw new MissingMemberException(type.FullName, name);

    private static MethodInfo FindMethod(Type type, string name) =>
        type.GetMethod(name) ?? type.GetInterfaces().Select(contract => contract.GetMethod(name))
            .FirstOrDefault(method => method is not null) ?? throw new MissingMethodException(type.FullName, name);

    private sealed class AssetRipperLibraries(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        private static readonly string? RuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);

        public Type GetType(string assembly, string name) =>
            LoadFromAssemblyName(new AssemblyName("AssetRipper." + assembly)).GetType("AssetRipper." + name, throwOnError: true)!;

        protected override Assembly? Load(AssemblyName assembly)
        {
            string file = assembly.Name + ".dll";
            string path = Path.Combine(directory, file);
            // Share the process runtime instead of loading copies from the self-contained release.
            if (RuntimeDirectory is not null)
                return File.Exists(Path.Combine(RuntimeDirectory, file)) || !File.Exists(path) ? null : LoadFromAssemblyPath(path);
            // Bundled runtime assemblies have no on-disk location.
            try { return Default.LoadFromAssemblyName(assembly); }
            catch (FileNotFoundException) { return File.Exists(path) ? LoadFromAssemblyPath(path) : null; }
        }
    }
}
