using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

/// <summary>
/// Sprite renderer covering planets (atlas frame animation) and trail particles (additive
/// blending). Each has its own shader program: Planet.vert/frag and Particle.vert/frag.
/// </summary>
public sealed unsafe class SpriteRenderer : IDisposable
{
    /// <summary>A planet sprite.</summary>
    public struct Planet
    {
        public Vector2 Center;
        public Vector2 HalfSize;
        public float Rotation;
        public Vector4 Tint;
        public int Frame;
    }

    /// <summary>A trail particle.</summary>
    public struct Particle
    {
        public Vector2 Center;
        public Vector2 HalfSize;
        public Vector4 Tint;
        public float Rotation;
        public int Frame;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ParticleInstance
    {
        public Vector2 Center;
        public Vector2 HalfSize;
        public Vector4 Tint;
        public float Rotation;
        public float Frame;
    }

    private const int MaxInstances = 16384;

    // Planet program ----
    private int _planetProgram;
    private readonly int _planetVao;
    private readonly int _planetVbo;
    private int _planetLvp;
    private int _planetViewRight, _planetViewUp;
    private int _planetLcenter;
    private int _planetLhalf;
    private int _planetLrot;
    private int _planetLtint;
    private int _planetLframe;
    private int _planetLframes;
    private int _planetLvertical;
    private int _planetLsphere;
    private int _planetLtexture;

    // Particle program ----
    private int _particleProgram;
    private readonly int _particleVao;
    private readonly int _particleVbo;
    private readonly int _particleInstanceVbo;
    private int _particleLvp;
    private int _particleViewRight, _particleViewUp;
    private int _particleLtexture;
    private int _particleLcolumns;
    private int _particleLstraightAlpha;
    private ParticleInstance[] _convertedParticles = [];

    // Textures ----
    private readonly int _planetTexture;
    private readonly int _particleTexture;

    // Atlas info ----
    private readonly int _frameCount;
    private readonly bool _vertical;
    private readonly Vector4 _sphereRect;

    private bool _disposed;

    /// <summary>
    /// Create from the embedded textures (planet.png and particle.png).
    /// </summary>
    public SpriteRenderer()
    {
        _frameCount = 11;

        // Planet atlas ----
        ImageDecoder.RgbaImage planet = ImageDecoder.Decode(EmbeddedAssets.Read("Resources.planet.png"));
        {
            TextureSize = new Vector2(planet.Width, planet.Height);
            _vertical = planet.Height % _frameCount == 0;

            byte[] pixels = planet.Pixels;
            ImageDecoder.FlipVertically(pixels, planet.Width, planet.Height);

            _sphereRect = MeasureSphere(pixels, planet.Width, planet.Height, _frameCount, _vertical);
            _planetTexture = ImageDecoder.Upload(pixels, planet.Width, planet.Height, generateMipmaps: true);
        }

        // Particle texture ----
        ImageDecoder.RgbaImage particle = ImageDecoder.Decode(EmbeddedAssets.Read("Resources.particle.png"));
        {
            ImageDecoder.FlipVertically(particle.Pixels, particle.Width, particle.Height);
            _particleTexture = ImageDecoder.Upload(particle.Pixels, particle.Width, particle.Height, generateMipmaps: true);
        }

        // Planet program ----
        (_planetVao, _planetVbo) = CreateQuad();
        BuildPlanetProgram();

        // Particle program ----
        (_particleVao, _particleVbo, _particleInstanceVbo) = CreateInstancedQuad();
        BuildParticleProgram();
    }

    /// <summary>Compile the planet shader program.</summary>
    private void BuildPlanetProgram()
    {
        int previous = _planetProgram;
        _planetProgram = LinkProgram("Planet.vert", "Planet.frag");

        _planetLvp = GL.GetUniformLocation(_planetProgram, "uViewProjection");
        _planetViewRight = GL.GetUniformLocation(_planetProgram, "uViewRight");
        _planetViewUp = GL.GetUniformLocation(_planetProgram, "uViewUp");
        _planetLcenter = GL.GetUniformLocation(_planetProgram, "uCenter");
        _planetLhalf = GL.GetUniformLocation(_planetProgram, "uHalfSize");
        _planetLrot = GL.GetUniformLocation(_planetProgram, "uRotation");
        _planetLtint = GL.GetUniformLocation(_planetProgram, "uTint");
        _planetLframe = GL.GetUniformLocation(_planetProgram, "uFrame");
        _planetLframes = GL.GetUniformLocation(_planetProgram, "uFrames");
        _planetLvertical = GL.GetUniformLocation(_planetProgram, "uVertical");
        _planetLsphere = GL.GetUniformLocation(_planetProgram, "uSphereRect");
        _planetLtexture = GL.GetUniformLocation(_planetProgram, "uPlanetTex");

        if (previous != 0)
        {
            GL.DeleteProgram(previous);
        }
    }

    /// <summary>Compile the particle shader program.</summary>
    private void BuildParticleProgram()
    {
        int previous = _particleProgram;
        _particleProgram = LinkProgram("Particle.vert", "Particle.frag");

        _particleLvp = GL.GetUniformLocation(_particleProgram, "uViewProjection");
        _particleViewRight = GL.GetUniformLocation(_particleProgram, "uViewRight");
        _particleViewUp = GL.GetUniformLocation(_particleProgram, "uViewUp");
        _particleLtexture = GL.GetUniformLocation(_particleProgram, "uParticleTex");
        _particleLcolumns = GL.GetUniformLocation(_particleProgram, "uColumns");
        _particleLstraightAlpha = GL.GetUniformLocation(_particleProgram, "uStraightAlpha");

        if (previous != 0)
        {
            GL.DeleteProgram(previous);
        }
    }

    /// <summary>
    /// Reload and recompile both shader programs for hot reload. On failure the previous programs are
    /// kept so a half-written shader does not blank the view.
    /// </summary>
    /// <param name="error">Failure message, null on success.</param>
    public bool ReloadShaders(out string? error)
    {
        ShaderLibrary.InvalidateCache();

        try
        {
            BuildPlanetProgram();
        }
        catch (Exception ex)
        {
            error = $"行星着色器: {ex.Message}";
            return false;
        }

        try
        {
            BuildParticleProgram();
        }
        catch (Exception ex)
        {
            error = $"粒子着色器: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Number of planet frames.</summary>
    public int FrameCount => _frameCount;

    /// <summary>Planet texture size.</summary>
    public Vector2 TextureSize { get; }

    /// <summary>Draw the planets.</summary>
    public void DrawPlanets(in Matrix4 viewProjection, ReadOnlySpan<Planet> planets) =>
        DrawPlanets(viewProjection, planets, scene: null);

    internal void DrawPlanets(in Matrix4 viewProjection, ReadOnlySpan<Planet> planets, SceneRenderView? scene)
    {
        if (planets.IsEmpty || _planetTexture == 0)
        {
            return;
        }

        GL.UseProgram(_planetProgram);

        Matrix4 vp = viewProjection;
        GL.UniformMatrix4(_planetLvp, false, ref vp);
        GL.Uniform3(_planetViewRight, scene?.Right ?? Vector3.UnitX);
        GL.Uniform3(_planetViewUp, scene?.Up ?? Vector3.UnitY);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _planetTexture);
        GL.Uniform1(_planetLtexture, 0);

        GL.Uniform1(_planetLframes, _frameCount);
        GL.Uniform1(_planetLvertical, _vertical ? 1 : 0);
        GL.Uniform4(_planetLsphere, _sphereRect.X, _sphereRect.Y, _sphereRect.Z, _sphereRect.W);

        GL.BindVertexArray(_planetVao);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        for (int i = 0; i < planets.Length; i++)
        {
            Planet planet = planets[i];

            GL.Uniform2(_planetLcenter, planet.Center.X, planet.Center.Y);
            GL.Uniform2(_planetLhalf, planet.HalfSize.X, planet.HalfSize.Y);
            GL.Uniform1(_planetLrot, planet.Rotation);
            GL.Uniform4(_planetLtint, planet.Tint.X, planet.Tint.Y, planet.Tint.Z, planet.Tint.W);
            GL.Uniform1(_planetLframe, planet.Frame);

            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.UseProgram(0);
    }

    /// <summary>
    /// Draw the particles using <c>Blend One OneMinusSrcAlpha</c> (premultiplied alpha).
    /// </summary>
    public void DrawParticles(in Matrix4 viewProjection, ReadOnlySpan<Particle> particles)
    {
        if (particles.IsEmpty || _particleTexture == 0)
            return;

        if (_convertedParticles.Length < particles.Length)
            _convertedParticles = new ParticleInstance[Math.Max(particles.Length, _convertedParticles.Length * 2)];
        for (int i = 0; i < particles.Length; i++)
        {
            ref readonly Particle particle = ref particles[i];
            _convertedParticles[i] = new ParticleInstance
            {
                Center = particle.Center, HalfSize = particle.HalfSize, Tint = particle.Tint,
                Rotation = particle.Rotation, Frame = particle.Frame,
            };
        }
        DrawParticleInstances(viewProjection, _convertedParticles.AsSpan(0, particles.Length));
    }

    internal void DrawParticleInstances(in Matrix4 viewProjection, ReadOnlySpan<ParticleInstance> particles,
        SceneRenderView? scene = null) =>
        DrawParticleInstances(viewProjection, particles, _particleTexture, 1, straightAlpha: false, scene);

    internal void DrawParticleInstances(in Matrix4 viewProjection, ReadOnlySpan<ParticleInstance> particles,
        int texture, int columns, bool straightAlpha, SceneRenderView? scene = null)
    {
        if (particles.IsEmpty || texture == 0)
        {
            return;
        }

        GL.UseProgram(_particleProgram);

        Matrix4 vp = viewProjection;
        GL.UniformMatrix4(_particleLvp, false, ref vp);
        GL.Uniform3(_particleViewRight, scene?.Right ?? Vector3.UnitX);
        GL.Uniform3(_particleViewUp, scene?.Up ?? Vector3.UnitY);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(_particleLtexture, 0);
        GL.Uniform1(_particleLcolumns, (float)columns);
        GL.Uniform1(_particleLstraightAlpha, straightAlpha ? 1 : 0);

        GL.BindVertexArray(_particleVao);

        // Alpha Blended Premultiply: Blend One OneMinusSrcAlpha, ColorMask RGB, ZWrite Off, Cull Off
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        GL.ColorMask(true, true, true, false);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        // Effects fill the vertex buffer layout directly, so uploading needs no staging copy.
        GL.BindBuffer(BufferTarget.ArrayBuffer, _particleInstanceVbo);
        fixed (ParticleInstance* data = particles)
        {
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                (nint)particles.Length * sizeof(ParticleInstance),
                (nint)data,
                BufferUsageHint.StreamDraw);
        }

        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, particles.Length);

        GL.ColorMask(true, true, true, true);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.UseProgram(0);
    }

    /// <summary>
    /// Build the instanced quad: attribute 0 is the corner, and instance attributes 1..3 carry the
    /// centre, half size and colour.
    /// </summary>
    private static (int Vao, int Vbo, int InstanceVbo) CreateInstancedQuad()
    {
        float[] corners =
        [
            -1f, -1f,
             1f, -1f,
            -1f,  1f,
             1f,  1f,
        ];

        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();
        int instanceVbo = GL.GenBuffer();

        GL.BindVertexArray(vao);

        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        fixed (float* data = corners)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * sizeof(float), (nint)data, BufferUsageHint.StaticDraw);
        }

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        // Centre, half size, colour, rotation and atlas frame.
        GL.BindBuffer(BufferTarget.ArrayBuffer, instanceVbo);
        int stride = sizeof(ParticleInstance);

        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.VertexAttribDivisor(1, 1);

        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.VertexAttribDivisor(2, 1);

        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.VertexAttribDivisor(3, 1);

        GL.EnableVertexAttribArray(4);
        GL.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.VertexAttribDivisor(4, 1);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        return (vao, vbo, instanceVbo);
    }

    private static (int Vao, int Vbo) CreateQuad()
    {
        float[] corners =
        [
            -1f, -1f,
             1f, -1f,
            -1f,  1f,
             1f,  1f,
        ];

        int vao = GL.GenVertexArray();
        int vbo = GL.GenBuffer();

        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        fixed (float* data = corners)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * sizeof(float), (nint)data, BufferUsageHint.StaticDraw);
        }

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        return (vao, vbo);
    }

    /// <summary>
    /// Measure the sphere's rect inside one frame cell as 0..1 fractions.
    /// </summary>
    private static Vector4 MeasureSphere(byte[] pixels, int width, int height, int frameCount, bool vertical)
    {
        const byte AlphaThreshold = 8;

        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        int cellWidth, cellHeight, left, top;

        if (vertical)
        {
            cellWidth = width;
            cellHeight = height / frameCount;
            left = 0;
            top = (frameCount / 2) * cellHeight;
        }
        else
        {
            cellWidth = width / frameCount;
            cellHeight = height;
            left = (frameCount / 2) * cellWidth;
            top = 0;
        }

        for (int y = top; y < top + cellHeight; y++)
        {
            int rowOffset = y * width * 4;
            for (int x = left; x < left + cellWidth; x++)
            {
                if (pixels[rowOffset + x * 4 + 3] <= AlphaThreshold)
                {
                    continue;
                }

                int localX = x - left;
                int localY = y - top;

                if (localX < minX) minX = localX;
                if (localX > maxX) maxX = localX;
                if (localY < minY) minY = localY;
                if (localY > maxY) maxY = localY;
            }
        }

        if (maxX < 0)
        {
            return new Vector4(0f, 0f, 1f, 1f);
        }

        return new Vector4(
            minX / (float)cellWidth,
            minY / (float)cellHeight,
            (maxX - minX + 1) / (float)cellWidth,
            (maxY - minY + 1) / (float)cellHeight);
    }

    internal static int LinkProgram(string vertexName, string fragmentName) =>
        LinkSources(ShaderLibrary.Load(vertexName), ShaderLibrary.Load(fragmentName));

    internal static int LinkSources(string vertexSource, string fragmentSource, Action<int>? bindAttributes = null)
    {
        int vertex = CompileShader(ShaderType.VertexShader, vertexSource);
        int fragment;
        try { fragment = CompileShader(ShaderType.FragmentShader, fragmentSource); }
        catch
        {
            GL.DeleteShader(vertex);
            throw;
        }

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        bindAttributes?.Invoke(program);
        GL.LinkProgram(program);

        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int status);
        if (status == 0)
        {
            string log = GL.GetProgramInfoLog(program);
            GL.DeleteProgram(program);
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            throw new InvalidOperationException($"着色器链接失败 / Shader link failed: {log}");
        }

        GL.DetachShader(program, vertex);
        GL.DetachShader(program, fragment);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);

        return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        string[] sources = [source];
        int[] lengths = [System.Text.Encoding.UTF8.GetByteCount(source)];
        GL.ShaderSource(shader, 1, sources, lengths);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new InvalidOperationException($"{type} 编译失败 / Compilation failed: {log}");
        }

        return shader;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        GL.DeleteVertexArray(_planetVao);
        GL.DeleteBuffer(_planetVbo);
        GL.DeleteProgram(_planetProgram);

        GL.DeleteVertexArray(_particleVao);
        GL.DeleteBuffer(_particleVbo);
        GL.DeleteBuffer(_particleInstanceVbo);
        GL.DeleteProgram(_particleProgram);

        if (_planetTexture != 0)
        {
            GL.DeleteTexture(_planetTexture);
        }

        if (_particleTexture != 0)
        {
            GL.DeleteTexture(_particleTexture);
        }

        _disposed = true;
    }
}

