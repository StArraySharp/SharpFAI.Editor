using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using Matrix4 = OpenTK.Mathematics.Matrix4;

namespace SharpFAI.Player.Engine.Filters;

internal sealed class CameraMotionBlurFilter(IFilterBackend backend) : IGeneratedFilter
{
    internal const string ShaderName = "Hidden/CameraMotionBlur";
    private Matrix4? _previousView;
    private IFilterMaterial? _material;
    public float VelocityScale { get; set; } = 0.375f;
    public IReadOnlyList<string> Parameters => [];
    public FilterParameterKind ParameterKind(string field) => FilterParameterKind.Float;
    public Vector4? GetParameter(string field) => null;
    public void SetParameter(string field, Vector4 value) { }
    public void Enable() { }

    public void Render(IFilterTarget source, IFilterTarget destination)
    {
        if (backend is not OpenGlFilterBackend gl)
        {
            backend.Blit(source, destination);
            return;
        }

        Matrix4 view = gl.CameraView;
        var previous = _previousView;
        _previousView = view;
        if (previous is null || previous.Value == view || VelocityScale == 0f)
        {
            backend.Blit(source, destination);
            return;
        }

        // The renderer is orthographic and composites its scene in 2D. Its geometry
        // shares one depth plane for velocity reconstruction.
        using var units = new AdditionalTextureBindings();
        var combined = Matrix4.Invert(view) * previous.Value;
        var material = _material ??= gl.CreateMaterial(gl.FindShader(ShaderName));
        material.SetVector("hlslcc_mtx4x4_ToPrevViewProjCombined[0]", ToNumerics(combined.Row0));
        material.SetVector("hlslcc_mtx4x4_ToPrevViewProjCombined[1]", ToNumerics(combined.Row1));
        material.SetVector("hlslcc_mtx4x4_ToPrevViewProjCombined[2]", ToNumerics(combined.Row2));
        material.SetVector("hlslcc_mtx4x4_ToPrevViewProjCombined[3]", ToNumerics(combined.Row3));
        material.SetVector("_ZBufferParams", Vector4.UnitY);
        material.SetFloat("_VelocityScale", VelocityScale);
        material.SetFloat("_SoftZDistance", 0.005f);
        material.SetFloat("_Jitter", 0.05f);
        material.SetTexture("_CameraDepthTexture", gl.FlatDepthTexture);

        const int maxVelocity = 8;
        int tileWidth = (source.Width + maxVelocity - 1) / maxVelocity;
        int tileHeight = (source.Height + maxVelocity - 1) / maxVelocity;
        int radius = source.Width / tileWidth;
        material.SetFloat("_MaxVelocity", radius);
        material.SetFloat("_MaxRadiusOrKInPaper", radius);
        var velocity = gl.RentHalfTarget(source.Width, source.Height);
        var tiles = gl.RentHalfTarget(tileWidth, tileHeight);
        var neighbors = gl.RentHalfTarget(tileWidth, tileHeight);
        try
        {
            velocity.Sampling = tiles.Sampling = neighbors.Sampling = FilterSampling.Point;
            material.SetTexture("_NoiseTex", gl.LoadTexture("MotionBlurJitter"));
            material.SetTexture("_VelTex", velocity);
            material.SetTexture("_NeighbourMaxTex", neighbors);
            gl.Blit(source, velocity, material, 0);
            gl.Blit(velocity, tiles, material, 2);
            gl.Blit(tiles, neighbors, material, 3);
            gl.Blit(source, destination, material, 4);
        }
        finally
        {
            gl.ReleaseTarget(neighbors);
            gl.ReleaseTarget(tiles);
            gl.ReleaseTarget(velocity);
        }
    }

    public void Dispose()
    {
        if (_material is { } material)
            backend.ReleaseMaterial(material);
        _material = null;
    }

    private static Vector4 ToNumerics(OpenTK.Mathematics.Vector4 value) => new(value.X, value.Y, value.Z, value.W);

    internal readonly struct AdditionalTextureBindings : IDisposable
    {
        private readonly TextureUnit _active = (TextureUnit)GL.GetInteger(GetPName.ActiveTexture);
        private readonly (int Texture, int Sampler) _depth;
        private readonly (int Texture, int Sampler) _noise;

        public AdditionalTextureBindings()
        {
            _depth = Capture(TextureUnit.Texture3);
            _noise = Capture(TextureUnit.Texture4);
            GL.ActiveTexture(_active);
        }

        private static (int Texture, int Sampler) Capture(TextureUnit unit)
        {
            GL.ActiveTexture(unit);
            return (GL.GetInteger(GetPName.TextureBinding2D), GL.GetInteger(GetPName.SamplerBinding));
        }

        public void Dispose()
        {
            if (_active == 0)
                return;
            Restore(3, _depth);
            Restore(4, _noise);
            GL.ActiveTexture(_active);
        }

        private static void Restore(int unit, (int Texture, int Sampler) binding)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, binding.Texture);
            GL.BindSampler(unit, binding.Sampler);
        }
    }
}
