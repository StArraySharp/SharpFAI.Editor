using System.Numerics;
using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine.Filters;

namespace SharpFAI.Editor;

internal static class EditorFilterCatalog
{
    public static string[] Names => GeneratedFilterRegistry.Factories.Keys.Order(StringComparer.Ordinal).ToArray();
    private static readonly Dictionary<(string Filter, bool Camera), EditorField[]> Cache = [];

    public static EditorField[] Fields(string filter, bool camera = true)
    {
        if (Cache.TryGetValue((filter, camera), out var fields)) return fields;
        if (!GeneratedFilterRegistry.MetadataFactories.TryGetValue(filter, out var factory)) return [];
        using var instance = factory(new MetadataBackend());
        if (camera) CameraFilterDefaults.Apply(instance, filter);
        fields = instance.Parameters.Select(name =>
        {
            Vector4 value = instance.GetParameter(name) ?? Vector4.Zero;
            var kind = instance.ParameterKind(name);
            bool percent = kind == FilterParameterKind.Float && !name.Contains("rotation", StringComparison.OrdinalIgnoreCase);
            JToken initial = kind switch
            {
                FilterParameterKind.Integer => new JValue((int)value.X),
                FilterParameterKind.Color => new JValue(EditorPropertyEdit.ColorHex(value)),
                FilterParameterKind.Vector2 => new JArray(value.X, value.Y),
                _ => new JValue(value.X * (percent ? 100 : 1))
            };
            var range = EditorControlMetadata.Range(filter, name);
            double multiplier = percent ? 100 : 1;
            return new EditorField("filter_" + name, kind switch
            {
                FilterParameterKind.Integer => "Int", FilterParameterKind.Color => "Color",
                FilterParameterKind.Vector2 => "Vector2", _ => "Float"
            }, initial, Unit: percent ? "%" : null, Optional: true, StartEnabled: false,
                Minimum: range?.Min * multiplier, Maximum: range?.Max * multiplier);
        }).ToArray();
        return Cache[(filter, camera)] = fields;
    }

    // Metadata extraction must not allocate OpenGL resources or depend on a current rendering context.
    private sealed class MetadataBackend : IFilterBackend, IFilterMaterial, IFilterShader
    {
        public float DeltaTime => 0;
        public int ScreenWidth => 128;
        public int ScreenHeight => 128;
        public IFilterShader FindShader(string name) => this;
        public IFilterTexture LoadTexture(string name) => new Target();
        public IFilterMaterial CreateMaterial(IFilterShader shader) => this;
        public void ReleaseMaterial(IFilterMaterial material) { }
        public IFilterTarget RentTarget(int width, int height, int depthBits) => new Target();
        public void ReleaseTarget(IFilterTarget target) { }
        public void Blit(IFilterTarget source, IFilterTarget destination, IFilterMaterial? material = null, int pass = -1) { }
        public void SetFloat(string name, float value) { }
        public void SetInt(string name, int value) { }
        public void SetVector(string name, Vector4 value) { }
        public void SetTexture(string name, IFilterTexture? value) { }
    }

    private sealed class Target : IFilterTarget
    {
        public int Width => 128;
        public int Height => 128;
        public FilterSampling Sampling { get; set; }
    }
}
