namespace SharpFAI.Player.Engine.Filters;

internal static class CameraPassOrder
{
    // OnRenderImage runs in the order of the components on Camera.prefab.
    // scrVfxPlus.MakeNewFilterDictionary only maps event names to those components.
    private static readonly Dictionary<string, int> PrefabOrder = new[]
    {
        "CameraFilterPackLegacy_Real_VHS",
        "CameraFilterPackLegacy_FX_Funk",
        "CameraFilterPackLegacy_TV_LED",
        "CameraFilterPackLegacy_Noise_TV",
        "CameraFilterPackLegacy_Distortion_FishEye",
        "CameraFilterPackLegacy_TV_50",
        "CameraFilterPackLegacy_TV_80",
        "CameraFilterPackLegacy_Drawing_Paper",
        "CameraFilterPackLegacy_TV_ARCADE",
        "CameraFilterPackLegacy_TV_CompressionFX",
        "CameraFilterPackLegacy_FX_Glitch1",
        "CameraFilterPackLegacy_Distortion_Wave_Horizontal",
        "CameraFilterPackLegacy_Oculus_NightVision1",
        "CameraFilterPackLegacy_Pixel_Pixelisation",
        "CameraFilterPackLegacy_Color_Sepia",
        "CameraFilterPackLegacy_Color_GrayScale",
        "CameraFilterPackLegacy_Atmosphere_Rain",
        "CameraFilterPackLegacy_Color_Invert",
        "CameraFilterPackLegacy_TV_Video3D",
        "CameraFilterPackLegacy_Blizzard",
        "CameraFilterPackLegacy_Edge_Neon",
        "CameraFilterPackLegacy_FX_8bits_gb",
        "CameraFilterPackLegacy_Atmosphere_Snow_8bits",
        "CameraMotionBlur",
        "CameraFilterPackLegacy_Film_Grain",
        VideoBloomPass.ShaderName,
        "CameraFilterPackLegacy_Color_Chromatic_Aberration",
        "ADOFAI/ScreenTile",
        "ADOFAI/ScreenScroll",
        "PolarScreen",
        "CameraFilterPackLegacy_FX_Hexagon_Black",
        "CameraFilterPackLegacy_TV_Posterize",
        "CameraFilterPackLegacy_Blur_Blurry",
        "CameraFilterPackLegacy_Blur_Focus",
        "CameraFilterPackLegacy_Sharpen_Sharpen",
        "CameraFilterPackLegacy_Color_Contrast",
        "CameraFilterPackLegacy_Edge_BlackLine",
        "CameraFilterPackLegacy_Pixel_Pixelisation_OilPaint",
        "CameraFilterPackLegacy_AAA_WaterDrop",
        "CameraFilterPackLegacy_Light_Water2",
        "CameraFilterPackLegacy_FX_superDot",
        "CameraFilterPackLegacy_Blur_GaussianBlur",
        "FallingPetals",
    }.Select((name, index) => (name, index)).ToDictionary(entry => entry.name, entry => entry.index, StringComparer.Ordinal);

    public static int Legacy(string name, int fallback) =>
        PrefabOrder.TryGetValue(name, out int index) ? index : PrefabOrder.Count + fallback;
}
