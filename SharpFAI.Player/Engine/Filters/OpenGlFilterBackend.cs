using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL4;
using SharpFAI.Player.Engine.ShaderGen;
using Thrust.Graphics;
using SavedState = Thrust.Graphics.GlRenderStateScope;

namespace SharpFAI.Player.Engine.Filters;

internal sealed class OpenGlFilterBackend(bool spritePool = false) : IFilterBackend, IDisposable
{
    private sealed record Shader(string Name) : IFilterShader;
    private sealed record Texture(int Handle, int Width, int Height) : IFilterTexture;
    internal sealed record Target(int Framebuffer, int Handle, int Width, int Height, int Depth = 0,
        int Renderbuffer = 0, int X = 0, int Y = 0, bool HalfFloat = false) : IFilterTarget
    {
        public FilterSampling Sampling { get; set; } = FilterSampling.Bilinear;
    }

    private sealed class Material(Shader shader) : IFilterMaterial
    {
        public Shader Shader { get; } = shader;
        public readonly Dictionary<string, float> Floats = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Ints = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Vector4> Vectors = new(StringComparer.Ordinal);
        public readonly Dictionary<string, IFilterTexture?> Textures = new(StringComparer.Ordinal);
        public void SetFloat(string name, float value) => Floats[name] = value;
        public void SetInt(string name, int value) => Ints[name] = value;
        public void SetVector(string name, Vector4 value) => Vectors[name] = value;
        public void SetTexture(string name, IFilterTexture? value) => Textures[name] = value;
    }

    private sealed class Program(int handle, int vertexArray = 0)
    {
        public int Handle { get; } = handle;
        public int VertexArray { get; } = vertexArray;
        private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);
        private readonly Dictionary<int, float> _floats = [];
        private readonly Dictionary<int, int> _ints = [];
        private readonly Dictionary<int, Vector4> _vectors = [];

        public void SetFloat(string name, float value)
        {
            int location = Location(name);
            if (location < 0 || _floats.TryGetValue(location, out float previous) && previous.Equals(value))
                return;
            GL.Uniform1(location, value);
            _floats[location] = value;
        }

        public void SetInt(string name, int value)
        {
            int location = Location(name);
            if (location < 0 || _ints.TryGetValue(location, out int previous) && previous == value)
                return;
            GL.Uniform1(location, value);
            _ints[location] = value;
        }

        public void SetVector(string name, Vector4 value)
        {
            int location = Location(name);
            if (location < 0 || _vectors.TryGetValue(location, out Vector4 previous) && previous.Equals(value))
                return;
            GL.Uniform4(location, value.X, value.Y, value.Z, value.W);
            _vectors[location] = value;
        }

        public int Location(string name)
        {
            if (!_locations.TryGetValue(name, out int location))
                _locations.Add(name, location = GL.GetUniformLocation(Handle, name));
            return location;
        }
    }

    private const string CopyFragment = """
        #version 410 core
        in vec2 vs_TEXCOORD0;
        out vec4 color;
        uniform sampler2D _MainTex;
        void main() { color = texture(_MainTex, vs_TEXCOORD0); }
        """;

    private const string RestoreSpriteAlphaFragment = """
        #version 410 core
        out vec4 color;
        uniform sampler2D _MainTex;
        uniform sampler2D _OriginalTex;
        void main()
        {
            ivec2 pixel = ivec2(gl_FragCoord.xy);
            color = vec4(texelFetch(_MainTex, pixel, 0).rgb, texelFetch(_OriginalTex, pixel, 0).a);
        }
        """;

    private const string FullscreenVertex = """
        #version 410 core
        out vec2 vs_TEXCOORD0;
        out vec4 vs_COLOR0;
        void main()
        {
            vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
            vs_TEXCOORD0 = uv;
            vs_COLOR0 = vec4(1.0);
        }
        """;

    private readonly Dictionary<(string Name, int Pass), Program?> _programs = [];
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shaderErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textureErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Width, int Height, int Depth, bool HalfFloat), Stack<Target>> _pool = [];
    private readonly List<Target> _borrowed = [];
    private long _pooledBytes;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly int _vao = GL.GenVertexArray();
    private int _unityQuad;
    private Program? _copy;
    private Program? _restoreSpriteAlpha;
    public float DeltaTime { get; private set; }
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    private double _time;

    public IFilterShader FindShader(string name) => new Shader(name);

    public IFilterTexture LoadTexture(string name)
    {
        if (!_textures.TryGetValue(name, out Texture? texture))
        {
            string filename = name + ".png";
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "Filters", filename);
            try
            {
                byte[]? bytes = File.Exists(path) ? File.ReadAllBytes(path) : EmbeddedAssets.Read("Resources.Filters." + filename);
                if (bytes is not null)
                {
                    var image = ImageDecoder.Decode(bytes);
                    ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
                    string metadata = File.Exists(path)
                        ? File.Exists(path + ".meta") ? File.ReadAllText(path + ".meta") : ""
                        : Encoding.UTF8.GetString(EmbeddedAssets.Read("Resources.Filters." + filename + ".meta") ?? []);
                    texture = new Texture(UploadTexture(image, metadata), image.Width, image.Height);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or SixLabors.ImageSharp.ImageFormatException)
            {
                _textureErrors[name] = ex.Message;
            }
            _textures.Add(name, texture);
        }

        return texture ?? throw new FilterResourceException("Texture " + name + ": " +
            _textureErrors.GetValueOrDefault(name, "missing"));
    }

    private static int UploadTexture(ImageDecoder.RgbaImage image, string metadata)
    {
        int Read(string name, int fallback)
        {
            var value = Regex.Match(metadata, @"(?m)^\s*" + name + @":[ \t]*(-?\d+)");
            return value.Success && int.TryParse(value.Groups[1].Value, out int number) ? number : fallback;
        }

        bool mipmaps = Read("enableMipMap", 1) != 0;
        int mode = Read("filterMode", 1);
        int texture = ImageDecoder.Upload(image, generateMipmaps: mipmaps);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var min = (mode, mipmaps) switch
        {
            (0, false) => TextureMinFilter.Nearest,
            (0, true) => TextureMinFilter.NearestMipmapNearest,
            (_, false) => TextureMinFilter.Linear,
            (2, true) => TextureMinFilter.LinearMipmapLinear,
            _ => TextureMinFilter.LinearMipmapNearest,
        };
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)(mode == 0 ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)Wrap(Read("wrapU", 0)));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)Wrap(Read("wrapV", 0)));
        return texture;
    }

    private static TextureWrapMode Wrap(int mode) => mode switch
    {
        0 => TextureWrapMode.Repeat,
        2 => TextureWrapMode.MirroredRepeat,
        _ => TextureWrapMode.ClampToEdge,
    };

    public IFilterMaterial CreateMaterial(IFilterShader shader) => new Material((Shader)shader);

    public void ReleaseMaterial(IFilterMaterial material)
    {
        // The material only owns its uniform values. Programs and textures are shared by the backend.
    }

    public IFilterTarget RentTarget(int width, int height, int depthBits)
        => RentTarget(width, height, depthBits, halfFloat: false);

    internal IFilterTarget RentHalfTarget(int width, int height)
        => RentTarget(width, height, depthBits: 0, halfFloat: true);

    private IFilterTarget RentTarget(int width, int height, int depthBits, bool halfFloat)
    {
        var key = (width, height, depthBits, halfFloat);
        if (!_pool.TryGetValue(key, out var targets))
            _pool.Add(key, targets = new());
        Target target;
        if (targets.TryPop(out Target? cached))
        {
            target = cached;
            _pooledBytes -= TargetBytes(target);
        }
        else
            target = CreateTarget(width, height, depthBits, halfFloat);
        target.Sampling = FilterSampling.Bilinear;
        _borrowed.Add(target);
        return target;
    }

    private static Target CreateTarget(int width, int height, int depth, bool halfFloat)
    {
        if (width <= 0 || height <= 0)
            throw new FilterResourceException($"Invalid filter target size {width}×{height}");
        int texture = GL.GenTexture();
        int framebuffer = GL.GenFramebuffer();
        int renderbuffer = 0;
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, halfFloat ? PixelInternalFormat.Rgba16f : PixelInternalFormat.Rgba8,
            width, height, 0, PixelFormat.Rgba, halfFloat ? PixelType.HalfFloat : PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, texture, 0);
        if (depth > 0)
        {
            renderbuffer = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, renderbuffer);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
                depth <= 16 ? RenderbufferStorage.DepthComponent16 : RenderbufferStorage.DepthComponent24, width, height);
            GL.FramebufferRenderbuffer(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, renderbuffer);
        }

        var target = new Target(framebuffer, texture, width, height, depth, renderbuffer, HalfFloat: halfFloat);
        if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) == FramebufferErrorCode.FramebufferComplete)
            return target;
        DeleteTarget(target);
        throw new FilterResourceException($"Incomplete {width}×{height} filter framebuffer");
    }

    public void ReleaseTarget(IFilterTarget target)
    {
        if (target is not Target owned || !_borrowed.Remove(owned))
            throw new InvalidOperationException("Filter target was not rented.");
        var pool = _pool[(owned.Width, owned.Height, owned.Depth, owned.HalfFloat)];
        // Bloom and multi-pass filters can need more than two targets of one size.
        // Retaining too few reallocates textures every frame even at a fixed resolution.
        if (pool.Count < (spritePool ? 3 : 4))
        {
            pool.Push(owned);
            _pooledBytes += TargetBytes(owned);
            long limit = (spritePool ? 96L : 192L) * 1024 * 1024;
            while (_pooledBytes > limit)
            {
                // Keep the current size hot where possible. The bound applies only
                // to idle textures; oversized active targets remain usable for a draw.
                var oldest = _pool.Values.FirstOrDefault(entries => entries.Count != 0 && entries != pool) ?? pool;
                Target discarded = oldest.Pop();
                _pooledBytes -= TargetBytes(discarded);
                DeleteTarget(discarded);
            }
        }
        else
            DeleteTarget(owned);
    }

    private static long TargetBytes(Target target) =>
        (long)target.Width * target.Height * ((target.HalfFloat ? 8 : 4) + (target.Depth > 0 ? 4 : 0));

    private static void DeleteTarget(Target target)
    {
        GL.DeleteFramebuffer(target.Framebuffer);
        GL.DeleteTexture(target.Handle);
        if (target.Renderbuffer != 0)
            GL.DeleteRenderbuffer(target.Renderbuffer);
    }

    public void Blit(IFilterTarget source, IFilterTarget destination, IFilterMaterial? material = null, int pass = -1) =>
        Blit((Target)source, (Target)destination, (Material?)material, pass, configureSampling: true);

    private void Blit(Target input, Target output, Material? parameters, int pass, bool configureSampling)
    {
        if (input.Framebuffer == output.Framebuffer)
            throw new InvalidOperationException("Filter source and destination must be distinct.");

        Program program = parameters is null ? _copy ??= Link(CopyFragment) :
            GetProgram(parameters.Shader.Name, Math.Max(0, pass));
        Bind(output);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.ColorMask(true, true, true, true);
        GL.UseProgram(program.Handle);
        GL.BindVertexArray(program.VertexArray == 0 ? _vao : program.VertexArray);
        BindTexture(program, "_MainTex", input.Handle, 0);
        if (configureSampling)
        {
            TextureMinFilter min = input.Sampling == FilterSampling.Point ? TextureMinFilter.Nearest : TextureMinFilter.Linear;
            TextureMagFilter mag = input.Sampling == FilterSampling.Point ? TextureMagFilter.Nearest : TextureMagFilter.Linear;
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)mag);
        }
        SetVector(program, "_MainTex_ST", new Vector4(1, 1, 0, 0));
        SetVector(program, "_MainTex_TexelSize", new Vector4(1f / input.Width, 1f / input.Height, input.Width, input.Height));
        SetVector(program, "_ScreenParams", new Vector4(output.Width, output.Height, 1f + 1f / output.Width, 1f + 1f / output.Height));
        float seconds = (float)_time;
        SetVector(program, "_Time", new Vector4(seconds / 20f, seconds, seconds * 2, seconds * 3));
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters.Floats)
                program.SetFloat(name, value);
            foreach (var (name, value) in parameters.Ints)
                program.SetInt(name, value);
            foreach (var (name, value) in parameters.Vectors)
                SetVector(program, name, value);

            int unit = 1;
            foreach (var (name, value) in parameters.Textures)
            {
                if (program.Location(name) < 0)
                    continue;
                int handle = value switch
                {
                    Texture texture => texture.Handle,
                    Target target => target.Handle,
                    _ => throw new FilterResourceException("Missing texture for " + name),
                };
                if (handle == output.Handle)
                    throw new FilterResourceException("Filter cannot sample its destination " + name);
                if (unit >= SavedState.TextureUnits)
                    throw new FilterResourceException("Too many texture bindings for " + parameters.Shader.Name);
                BindTexture(program, name, handle, unit++);
            }
        }

        if (program.VertexArray == 0)
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        else
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    private static void BindTexture(Program program, string name, int handle, int unit)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, handle);
        GL.BindSampler(unit, 0);
        program.SetInt(name, unit);
    }

    private static void SetVector(Program program, string name, Vector4 value) =>
        program.SetVector(name, value);

    private Program GetProgram(string shader, int pass)
    {
        var key = (shader, pass);
        string? name = null;
        if (!_programs.TryGetValue(key, out Program? program))
        {
            name = pass == 0 ? shader : $"{shader}.pass-{pass}";
            try
            {
                string asset = "Filters/" + name;
                if (name == VideoBloomPass.ShaderName || name.StartsWith(VideoBloomPass.ShaderName + ".pass-", StringComparison.Ordinal))
                {
                    string original = ShaderLibrary.Load(asset + ".glsl");
                    program = LinkUnityFullscreen(original);
                }
                else
                {
                    string fragment = ShaderLibrary.TryLoad(asset + ".frag") ??
                        UnityShaderAdapter.ImportFullscreenFragment(ShaderLibrary.Load(asset + ".glsl"));
                    program = Link(fragment);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or FilterResourceException)
            {
                _shaderErrors[name] = ex.Message;
            }
            _programs.Add(key, program);
        }

        if (program is not null)
            return program;
        name ??= pass == 0 ? shader : $"{shader}.pass-{pass}";
        throw new FilterResourceException("Shader " + name + ": " + _shaderErrors[name]);
    }

    private Program LinkUnityFullscreen(string original)
    {
        int handle = SpriteRenderer.LinkSources(UnityShaderAdapter.ImportExportedVertex(original),
            UnityShaderAdapter.ImportFullscreenFragment(original));
        int vertexArray = GL.GenVertexArray();
        if (_unityQuad == 0)
        {
            _unityQuad = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _unityQuad);
            float[] quad = [-1f, -1f, 0f, 0f, 1f, -1f, 1f, 0f, -1f, 1f, 0f, 1f, 1f, 1f, 1f, 1f];
            GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.StaticDraw);
        }

        GL.BindVertexArray(vertexArray);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _unityQuad);
        BindAttribute("in_POSITION0", 0);
        BindAttribute("in_TEXCOORD0", 2 * sizeof(float));
        GL.UseProgram(handle);
        var program = new Program(handle, vertexArray);
        for (int index = 0; index < 4; index++)
        {
            var axis = index switch { 0 => Vector4.UnitX, 1 => Vector4.UnitY, 2 => Vector4.UnitZ, _ => Vector4.UnitW };
            SetVector(program, $"hlslcc_mtx4x4unity_ObjectToWorld[{index}]", axis);
            SetVector(program, $"hlslcc_mtx4x4unity_MatrixVP[{index}]", axis);
        }
        return program;

        void BindAttribute(string name, int offset)
        {
            int location = GL.GetAttribLocation(handle, name);
            if (location < 0)
                return;
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(location, 2, VertexAttribPointerType.Float, false, sizeof(float) * 4, offset);
        }
    }

    private static Program Link(string fragment)
    {
        int vertex = Compile(ShaderType.VertexShader, FullscreenVertex);
        int pixel = 0;
        int handle = GL.CreateProgram();
        try
        {
            pixel = Compile(ShaderType.FragmentShader, fragment);
            GL.AttachShader(handle, vertex);
            GL.AttachShader(handle, pixel);
            GL.LinkProgram(handle);
            GL.GetProgram(handle, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
                throw new FilterResourceException(GL.GetProgramInfoLog(handle));
            return new Program(handle);
        }
        catch
        {
            GL.DeleteProgram(handle);
            throw;
        }
        finally
        {
            GL.DeleteShader(vertex);
            if (pixel != 0)
                GL.DeleteShader(pixel);
        }
    }

    private static int Compile(ShaderType stage, string source)
    {
        int shader = GL.CreateShader(stage);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled != 0)
            return shader;

        string log = GL.GetShaderInfoLog(shader);
        GL.DeleteShader(shader);
        throw new FilterResourceException(log);
    }

    public void Draw(double time, float delta, IReadOnlyList<FilterTimeline.Entry> entries, Action drawScene)
    {
        using var state = new SavedState();
        Target screen = new(state.Framebuffer, 0, state.Width, state.Height, X: state.X, Y: state.Y);
        if (screen.Width <= 0 || screen.Height <= 0)
            return;

        _time = time;
        DeltaTime = delta;
        ScreenWidth = screen.Width;
        ScreenHeight = screen.Height;
        int rentedBefore = _borrowed.Count;
        try
        {
            TrimPool(screen.Width, screen.Height);
            Target first = (Target)RentTarget(screen.Width, screen.Height, 0);
            Target? second = null;
            Bind(first);
            GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            drawScene();
            Target input = first;
            for (int index = 0; index < entries.Count; index++)
            {
                Target output = index == entries.Count - 1 ? screen :
                    ReferenceEquals(input, first) ? second ??= (Target)RentTarget(screen.Width, screen.Height, 0) : first;
                RenderEntry(entries[index], input, output);
                input = output;
            }
        }
        finally
        {
            ReleaseSince(rentedBefore);
        }
    }

    public Target? FilterSprite(int texture, int width, int height, double time, float delta,
        IReadOnlyList<FilterTimeline.Entry> entries, bool smoothing, bool repeatX, bool repeatY)
    {
        if (entries.Count == 0)
            return null;

        using var state = new SavedState();
        _time = time;
        DeltaTime = delta;
        ScreenWidth = state.Width;
        ScreenHeight = state.Height;
        int initial = _borrowed.Count;
        Target? result = null;
        try
        {
            Target output = (Target)RentTarget(width, height, 0);
            Target first = (Target)RentTarget(width, height, 0);
            Blit(new Target(-1, texture, width, height), first, null, -1, configureSampling: false);
            Target input = first;
            foreach (var entry in entries)
            {
                Target next = ReferenceEquals(input, first) ? output : first;
                RenderEntry(entry, input, next);
                input = next;
            }

            // Restore alpha into the other ping-pong target. The original texture
            // stays external, so a third target is unnecessary even for long chains.
            output = ReferenceEquals(input, first) ? output : first;
            Bind(output);
            var program = _restoreSpriteAlpha ??= Link(RestoreSpriteAlphaFragment);
            GL.UseProgram(program.Handle);
            GL.BindVertexArray(_vao);
            BindTexture(program, "_MainTex", input.Handle, 0);
            BindTexture(program, "_OriginalTex", texture, 1);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, output.Handle);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)(repeatX ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)(repeatY ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)(smoothing ? TextureMinFilter.Linear : TextureMinFilter.Nearest));
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)(smoothing ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
            result = output;
            return result;
        }
        catch (FilterResourceException error)
        {
            Report("Decoration filter: " + error.Message);
            return null;
        }
        finally
        {
            for (int index = _borrowed.Count - 1; index >= initial; index--)
                if (!ReferenceEquals(_borrowed[index], result))
                    ReleaseTarget(_borrowed[index]);
        }
    }

    private void RenderEntry(FilterTimeline.Entry entry, Target input, Target output)
    {
        int initial = _borrowed.Count;
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            entry.Filter.Render(input, output);
        }
        catch (Exception error) when (error is FilterResourceException or ArithmeticException)
        {
            // Build the stack trace only once for repeated failures in a broken effect.
            if (_reported.Add(entry.Name + ": " + error.Message))
                Console.Error.WriteLine("ADOFAI filter: " + entry.Name + ": " + error);
            Blit(input, output);
        }
        finally
        {
            ReleaseSince(initial);
        }
    }

    private (int Width, int Height) _lastSize;

    private void TrimPool(int width, int height)
    {
        if (_lastSize == (width, height))
            return;
        foreach (var key in _pool.Keys.Where(key => (key.Width, key.Height) == _lastSize).ToArray())
        {
            foreach (Target target in _pool[key])
            {
                _pooledBytes -= TargetBytes(target);
                DeleteTarget(target);
            }
            _pool.Remove(key);
        }
        _lastSize = (width, height);
    }

    private void ReleaseSince(int count)
    {
        while (_borrowed.Count > count)
            ReleaseTarget(_borrowed[^1]);
    }

    private static void Bind(Target target)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target.Framebuffer);
        GL.Viewport(target.X, target.Y, target.Width, target.Height);
    }

    private void Report(string message)
    {
        if (_reported.Add(message))
            Console.Error.WriteLine("ADOFAI filter: " + message);
    }

    public void Dispose()
    {
        ReleaseSince(0);
        foreach (var pool in _pool.Values)
            foreach (var target in pool)
                DeleteTarget(target);
        foreach (var texture in _textures.Values)
            if (texture is not null)
                GL.DeleteTexture(texture.Handle);
        foreach (var program in _programs.Values)
            if (program is not null)
            {
                GL.DeleteProgram(program.Handle);
                if (program.VertexArray != 0)
                    GL.DeleteVertexArray(program.VertexArray);
            }
        if (_copy is not null)
            GL.DeleteProgram(_copy.Handle);
        if (_restoreSpriteAlpha is not null)
            GL.DeleteProgram(_restoreSpriteAlpha.Handle);
        GL.DeleteVertexArray(_vao);
        if (_unityQuad != 0)
            GL.DeleteBuffer(_unityQuad);
    }

}

internal sealed class FilterResourceException(string message) : Exception(message);
