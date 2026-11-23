namespace SharpFAI.Player.Engine.Filters;

internal static class LegacyCameraFilters
{
    private static readonly string[] EventNames =
    [
        "Grayscale", "Sepia", "Invert", "VHS", "EightiesTV", "FiftiesTV", "Arcade", "LED", "Rain",
        "Blizzard", "PixelSnow", "Compression", "Glitch", "Pixelate", "Waves", "Static", "Grain",
        "MotionBlur", "Fisheye", "Aberration", "Drawing", "Neon", "Handheld", "NightVision", "Funk",
        "Tunnel", "Weird3D", "Blur", "BlurFocus", "GaussianBlur", "HexagonBlack", "Posterize", "Sharpen",
        "Contrast", "EdgeBlackLine", "OilPaint", "SuperDot", "WaterDrop", "LightWater", "Petals", "PetalsInstant",
    ];

    public static readonly Dictionary<string, LegacyFilterBinding> Bindings = new(GeneratedFilterRegistry.Legacy, StringComparer.Ordinal)
    {
        ["MotionBlur"] = new("CameraMotionBlur", 0f, 0,
            (filter, intensity) => ((CameraMotionBlurFilter)filter).VelocityScale = 0.375f * intensity),
        ["Petals"] = new("FallingPetals", 0f, 0, null,
            (filter, time) => ((FallingPetalsFilter)filter).Restart(time, 6)),
        ["PetalsInstant"] = new("FallingPetals", 0f, 0, null,
            (filter, time) => ((FallingPetalsFilter)filter).Restart(time, 28)),
    };

    public static readonly Dictionary<string, Func<IFilterBackend, IGeneratedFilter>> Factories = new(StringComparer.Ordinal)
    {
        ["CameraMotionBlur"] = backend => new CameraMotionBlurFilter(backend),
        ["FallingPetals"] = backend => new FallingPetalsFilter(backend),
    };

    public static readonly HashSet<string> Components = Bindings.Values.Select(binding => binding.Component)
        .ToHashSet(StringComparer.Ordinal);

    public static readonly Dictionary<string, string> ShaderNames = new(StringComparer.Ordinal)
    {
        ["CameraMotionBlur"] = CameraMotionBlurFilter.ShaderName,
    };

    public static string DecodeName(string name) => int.TryParse(name, out int id) &&
        (uint)id < (uint)EventNames.Length ? EventNames[id] : name;
}
