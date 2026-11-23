using System.Numerics;

namespace SharpFAI.Player.Engine.Filters;

internal interface IGeneratedFilter : IDisposable
{
    void Enable();
    IReadOnlyList<string> Parameters { get; }
    FilterParameterKind ParameterKind(string field);
    Vector4? GetParameter(string field);
    void SetParameter(string field, Vector4 value);
    void Render(IFilterTarget source, IFilterTarget destination);
}

internal interface IFilterBackend
{
    float DeltaTime { get; }
    int ScreenWidth { get; }
    int ScreenHeight { get; }
    bool IsPlaying => true;
    IFilterShader? FindShader(string name);
    IFilterTexture? LoadTexture(string name);
    IFilterMaterial CreateMaterial(IFilterShader shader);
    void ReleaseMaterial(IFilterMaterial material);
    IFilterTarget RentTarget(int width, int height, int depthBits);
    void ReleaseTarget(IFilterTarget target);
    void Blit(IFilterTarget source, IFilterTarget destination, IFilterMaterial? material = null, int pass = -1);
}

internal readonly record struct LegacyFilterBinding(string Component, float DefaultIntensity, int Order,
    Action<IGeneratedFilter, float>? SetIntensity, Action<IGeneratedFilter, double>? OnEnabled = null);

internal interface IFilterShader;

internal interface IFilterTexture
{
    int Width { get; }
    int Height { get; }
}

internal interface IFilterTarget : IFilterTexture
{
    FilterSampling Sampling { get; set; }
}

internal enum FilterSampling { Point, Bilinear, Trilinear }
internal enum FilterParameterKind { Float, Integer, Vector2, Color }

internal interface IFilterMaterial
{
    void SetFloat(string name, float value);
    void SetInt(string name, int value);
    void SetVector(string name, Vector4 value);
    void SetVector(string name, Vector2 value) => SetVector(name, new Vector4(value, 0f, 0f));
    void SetTexture(string name, IFilterTexture? value);
}
