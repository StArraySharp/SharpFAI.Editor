using System.Numerics;

namespace SharpFAI.Player.Engine.Filters;

internal static class CameraFilterDefaults
{
    // Camera.prefab overrides script initializers before Awake copies values into static controls.
    // Keep these scoped to preinstalled camera components, not newly added decoration filters.
    public static void Apply(IGeneratedFilter filter, string component)
    {
        switch (component)
        {
            case "CameraFilterPackLegacy_Drawing_Paper":
                filter.SetParameter("Pencil_Color", new Vector4(0f, 0f, 0f, 1f));
                Set("Pencil_Correction", 1.1f);
                break;
            case "CameraFilterPackLegacy_TV_CompressionFX":
                Set("Parasite", 3f);
                break;
            case "CameraFilterPackLegacy_Pixel_Pixelisation":
                Set("_Pixelisation", 4f);
                break;
            case "CameraFilterPackLegacy_Edge_Neon":
                Set("EdgeWeight", 7.5f);
                break;
            case "CameraFilterPackLegacy_FX_8bits_gb":
                Set("Brightness", 0.2f);
                break;
            case "CameraFilterPackLegacy_Blur_Blurry":
                Set("FastFilter", 1f);
                break;
            case "CameraFilterPackLegacy_Blur_GaussianBlur":
                Set("Size", 1f);
                break;
        }

        void Set(string field, float value) => filter.SetParameter(field, new Vector4(value, 0f, 0f, 0f));
    }
}
