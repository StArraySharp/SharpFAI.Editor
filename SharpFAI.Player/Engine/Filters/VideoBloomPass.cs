using System.Numerics;

namespace SharpFAI.Player.Engine.Filters;

/// <summary>Runs the camera prefab's VideoBloom configuration with the original material passes.</summary>
internal sealed class VideoBloomPass(OpenGlFilterBackend backend) : IGeneratedFilter
{
    public const string ShaderName = "Hidden/VideoBloom";
    private readonly IFilterMaterial _material = backend.CreateMaterial(backend.FindShader(ShaderName)!);
    public float Threshold { get; set; }
    public float Intensity { get; set; }
    public Vector4 Tint { get; set; }

    public void Render(IFilterTarget source, IFilterTarget destination)
    {
        if (Intensity == 0f)
        {
            backend.Blit(source, destination);
            return;
        }

        float ratio = 198f / source.Height;
        int width = Math.Max(1, (int)(ratio * source.Width));
        const int height = 198;
        var thresholded = backend.RentHalfTarget(width, height);
        var medium = backend.RentHalfTarget(width, height);
        _material.SetFloat("_Param2", Threshold);
        if (ratio > .99f)
            backend.Blit(source, thresholded, _material, 2);
        else
        {
            var reduced = backend.RentHalfTarget(width * 2, height * 2);
            backend.Blit(source, reduced);
            backend.Blit(reduced, thresholded, _material, 2);
            backend.ReleaseTarget(reduced);
        }

        Blur(thresholded, medium, 50f * ratio);
        _material.SetTexture("_MediumBloom", medium);
        // The camera prefab has LargeAmount = 0. The shader still exposes this sampler.
        _material.SetTexture("_LargeBloom", medium);
        _material.SetVector("_Param0", new Vector4(.5f * Intensity, 0f, 0f, 0f));
        _material.SetVector("_Param1", new Vector4(Tint.X, Tint.Y, Tint.Z, 1f));
        backend.Blit(source, destination, _material, 3);
        backend.ReleaseTarget(thresholded);
        backend.ReleaseTarget(medium);
    }

    private void Blur(IFilterTarget source, IFilterTarget destination, float radius)
    {
        double blurScale = radius < 10 ? .1 * radius * 1.468417 :
            radius < 36.3287 ? .127368 * radius + .194737 : .8 * MathF.Sqrt(radius);
        int wholePasses = (int)blurScale;
        float fractional = (float)(blurScale - wholePasses);
        float spread = 1f;
        float sign = wholePasses == 0 ? -1f : 1f;
        // The threshold target is already half-float at the blur resolution.
        // Read it directly instead of copying it into another identical target.
        var current = source;
        for (int i = 0; i < wholePasses; i++)
        {
            sign = i % 2 == 0 ? 1f : -1f;
            _material.SetVector("_Param0", BlurOffsets(sign, spread));
            var next = backend.RentHalfTarget(source.Width, source.Height);
            backend.Blit(current, next, _material, 0);
            if (!ReferenceEquals(current, source))
                backend.ReleaseTarget(current);
            current = next;
            spread *= 1.4142135f;
        }
        _material.SetVector("_Param0", BlurOffsets(-sign, spread));
        _material.SetFloat("_Param2", fractional);
        backend.Blit(current, destination, _material, 1);
        if (!ReferenceEquals(current, source))
            backend.ReleaseTarget(current);
    }

    private static Vector4 BlurOffsets(float sign, float spread) =>
        new(sign * spread * 1.3333334f, spread / 3f, sign * spread / 3f, -spread * 1.3333334f);

    public IReadOnlyList<string> Parameters => [];
    public FilterParameterKind ParameterKind(string field) => FilterParameterKind.Float;
    public Vector4? GetParameter(string field) => null;
    public void SetParameter(string field, Vector4 value) { }
    public void Enable() { }
    public void Dispose() => backend.ReleaseMaterial(_material);
}
