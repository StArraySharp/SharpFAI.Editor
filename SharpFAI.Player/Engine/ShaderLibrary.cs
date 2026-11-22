namespace SharpFAI.Player.Engine;

/// <summary>
/// The editor's shader source loader. An on-disk copy wins so a shader can be edited and hot-reloaded;
/// when the program runs as a single-file bundle there is no such folder, so the embedded resource is
/// used instead.
/// </summary>
public static class ShaderLibrary
{
    private static readonly Dictionary<string, string> Cache = [];

    /// <summary>On-disk shader folder.</summary>
    public static string DiskDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Resources", "Shaders");

    /// <summary>Clear the cache so sources are re-read.</summary>
    public static void InvalidateCache() => Cache.Clear();

    /// <summary>
    /// Read a shader source, preferring the on-disk copy and falling back to the embedded one.
    /// </summary>
    /// <param name="name">File name, e.g. "Circle.vert".</param>
    public static string Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Cache.TryGetValue(name, out string? cached))
        {
            return cached;
        }

        string path = Path.Combine(DiskDirectory, name);

        if (File.Exists(path))
        {
            string disk = File.ReadAllText(path, System.Text.Encoding.UTF8);
            Cache[name] = disk;
            return disk;
        }

        // Single-file builds carry no loose files; the source is embedded in the assembly.
        byte[]? embedded = EmbeddedAssets.Read("Resources.Shaders." + name);

        if (embedded is not null)
        {
            string text = System.Text.Encoding.UTF8.GetString(embedded);
            Cache[name] = text;
            return text;
        }

        throw new FileNotFoundException($"Shader not found: {name}", path);
    }

    /// <summary>
    /// Resolve a shader for engine renderers: return null when the on-disk copy exists so they read it
    /// themselves, otherwise serve the embedded copy. That keeps hot reload working when the folder is
    /// present and still runs from a single-file bundle.
    /// </summary>
    public static string? ResolveForEngine(string name)
    {
        if (File.Exists(Path.Combine(DiskDirectory, name)))
        {
            return null;
        }

        return Load(name);
    }

    /// <summary>Try to load, returning null on failure.</summary>
    public static string? TryLoad(string name)
    {
        try
        {
            return Load(name);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
