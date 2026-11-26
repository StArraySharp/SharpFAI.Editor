using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using SharpFAI.Player.Engine.ShaderGen;
using Thrust.Graphics;
using SavedState = Thrust.Graphics.GlRenderStateScope;

namespace SharpFAI.Player.Engine.Filters;

internal sealed class OpenGlFilterBackend : IFilterBackend, IDisposable
{
    internal const int FilterTextureUnits = 5;
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
        public void SetFloat(string name, float value)
        {
            Ints.Remove(name);
            Floats[name] = value;
        }

        public void SetInt(string name, int value)
        {
            Floats.Remove(name);
            Ints[name] = value;
        }

        public void SetVector(string name, Vector4 value) => Vectors[name] = value;
        public void SetTexture(string name, IFilterTexture? value) => Textures[name] = value;
    }

    private sealed class Program(int handle, int vertexArray = 0, int colorAttribute = -1)
    {
        public int Handle { get; } = handle;
        public int VertexArray { get; } = vertexArray;
        public int ColorAttribute { get; } = colorAttribute;
        private readonly Dictionary<string, ActiveUniformType> _uniformTypes = ReadUniformTypes(handle);
        private readonly Dictionary<string, int> _locations = new(StringComparer.Ordinal);
        private readonly Dictionary<int, float> _floats = [];
        private readonly Dictionary<int, int> _ints = [];
        private readonly Dictionary<int, Vector4> _vectors = [];

        public void SetFloat(string name, float value)
        {
            int location = Location(name);
            if (location < 0)
                return;
            switch (_uniformTypes[name])
            {
                case ActiveUniformType.Float: SetFloat(location, value); break;
                case ActiveUniformType.Int: SetInt(location, (int)value); break;
                case ActiveUniformType.Bool: SetInt(location, value != 0f ? 1 : 0); break;
                case ActiveUniformType.UnsignedInt: SetUnsignedInt(location, (uint)value); break;
            }
        }

        private void SetFloat(int location, float value)
        {
            if (_floats.TryGetValue(location, out float previous) && previous.Equals(value))
                return;
            GL.Uniform1(location, value);
            _floats[location] = value;
        }

        public void SetInt(string name, int value)
        {
            int location = Location(name);
            if (location < 0)
                return;
            switch (_uniformTypes[name])
            {
                case ActiveUniformType.Float: SetFloat(location, value); break;
                case ActiveUniformType.Int:
                case ActiveUniformType.Bool:
                case ActiveUniformType.Sampler2D:
                case ActiveUniformType.Sampler3D:
                    SetInt(location, value);
                    break;
                case ActiveUniformType.UnsignedInt: SetUnsignedInt(location, (uint)value); break;
            }
        }

        private void SetInt(int location, int value)
        {
            if (_ints.TryGetValue(location, out int previous) && previous == value)
                return;
            GL.Uniform1(location, value);
            _ints[location] = value;
        }

        private void SetUnsignedInt(int location, uint value)
        {
            int bits = unchecked((int)value);
            if (_ints.TryGetValue(location, out int previous) && previous == bits)
                return;
            GL.Uniform1(location, value);
            _ints[location] = bits;
        }

        public void SetVector(string name, Vector4 value)
        {
            int location = Location(name);
            if (location < 0)
                return;
            ActiveUniformType type = _uniformTypes[name];
            if (type == ActiveUniformType.Float)
            {
                SetFloat(location, value.X);
                return;
            }
            if (_vectors.TryGetValue(location, out Vector4 previous) && previous.Equals(value))
                return;
            switch (type)
            {
                case ActiveUniformType.FloatVec2: GL.Uniform2(location, value.X, value.Y); break;
                case ActiveUniformType.FloatVec3: GL.Uniform3(location, value.X, value.Y, value.Z); break;
                case ActiveUniformType.FloatVec4: GL.Uniform4(location, value.X, value.Y, value.Z, value.W); break;
                default: return;
            }
            _vectors[location] = value;
        }

        public int Location(string name)
        {
            if (!_locations.TryGetValue(name, out int location))
                _locations.Add(name, location = _uniformTypes.ContainsKey(name) ? GL.GetUniformLocation(Handle, name) : -1);
            return location;
        }

        private static Dictionary<string, ActiveUniformType> ReadUniformTypes(int handle)
        {
            var result = new Dictionary<string, ActiveUniformType>(StringComparer.Ordinal);
            GL.GetProgram(handle, GetProgramParameterName.ActiveUniforms, out int count);
            for (int index = 0; index < count; index++)
            {
                string name = GL.GetActiveUniform(handle, index, out int elements, out ActiveUniformType type);
                result[name] = type;
                int element = name.IndexOf("[0]", StringComparison.Ordinal);
                if (element < 0)
                    continue;
                if (element + 3 == name.Length)
                    result[name[..element]] = type;
                for (int i = 1; i < elements; i++)
                    result[name[..element] + $"[{i}]" + name[(element + 3)..]] = type;
            }
            return result;
        }
    }

    private const string CopyFragment = """
        #version 410 core
        in vec2 vs_TEXCOORD0;
        out vec4 color;
        uniform sampler2D _MainTex;
        void main() { color = texture(_MainTex, vs_TEXCOORD0); }
        """;

    private const string ModColorFragment = """
        #version 410 core
        in vec2 vs_TEXCOORD0;
        out vec4 color;
        uniform sampler2D _MainTex;
        uniform vec4 _Multiply;
        uniform vec4 _Add;
        void main() { color = texture(_MainTex, vs_TEXCOORD0) * _Multiply + _Add; }
        """;
    private Program? _modColor;
    private readonly Dictionary<string, Program> _modShaders = new(StringComparer.Ordinal);
    private sealed record ModShaderInputs(Vector4 Parameters, float Time, IReadOnlyList<IFilterTarget> Textures,
        int DepthTexture, Target? History);
    private sealed class ModHistory
    {
        public double Time = double.NaN;
        public Target? Previous;
        public Target? Current;
    }
    private readonly Dictionary<string, ModHistory> _modHistory = new(StringComparer.Ordinal);

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
    private readonly Dictionary<string, ShaderLabDefaults?> _shaderDefaults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shaderErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _textureErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Width, int Height, int Depth, bool HalfFloat), Stack<Target>> _pool = [];
    private readonly List<Target> _borrowed = [];
    private long _pooledBytes;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly int _vao = GL.GenVertexArray();
    private int _unityQuad;
    private Program? _copy;
    private readonly CameraFilterOutputHistory<Target> _cameraHistory = new();
    private readonly CameraFilterOutputHistory<Target> _backgroundHistory = new();
    private readonly int[] _cameraViewport = new int[4];
    public float DeltaTime { get; private set; }
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    internal OpenTK.Mathematics.Matrix4 CameraView { get; set; }
    private double _time;
    private Vector4? _cameraScreenParams;

    public IFilterShader FindShader(string name) => new Shader(name);

    public IFilterTexture LoadTexture(string name)
    {
        if (!_textures.TryGetValue(name, out Texture? texture))
        {
            string filename = name + ".png";
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "Filters", filename);
            try
            {
                bool loose = File.Exists(path);
                string resourceName = "Resources.Filters." + filename;
                byte[]? bytes = loose ? File.ReadAllBytes(path) :
                    EmbeddedAssets.Read(GeneratedFilterAssetDefaults.TextureAliases.GetValueOrDefault(resourceName, resourceName));
                if (bytes is not null)
                {
                    var image = ImageDecoder.Decode(bytes);
                    ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
                    var sampling = loose && File.Exists(path + ".meta")
                        ? FilterTextureSampling.Parse(File.ReadAllText(path + ".meta"))
                        : GeneratedFilterAssetDefaults.Textures.GetValueOrDefault(filename, FilterTextureSampling.Default);
                    texture = new Texture(UploadTexture(image, sampling, point: name == "MotionBlurJitter"), image.Width, image.Height);
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

    private static int UploadTexture(ImageDecoder.RgbaImage image, FilterTextureSampling sampling, bool point)
    {
        bool mipmaps = sampling.Mipmaps;
        int mode = point ? 0 : sampling.FilterMode;
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
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)Wrap(sampling.WrapU));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)Wrap(sampling.WrapV));
        return texture;
    }

    private static TextureWrapMode Wrap(int mode) => mode switch
    {
        0 => TextureWrapMode.Repeat,
        2 => TextureWrapMode.MirroredRepeat,
        _ => TextureWrapMode.ClampToEdge,
    };

    public IFilterMaterial CreateMaterial(IFilterShader shader)
    {
        var material = new Material((Shader)shader);
        string name = material.Shader.Name;
        if (!_shaderDefaults.TryGetValue(name, out ShaderLabDefaults? defaults))
        {
            string? source = ShaderLibrary.TryLoad("Filters/" + name + ".shaderlab");
            defaults = source is null ? GeneratedFilterAssetDefaults.Shaders.GetValueOrDefault(name) : ShaderLabDefaults.Parse(source);
            _shaderDefaults.Add(name, defaults);
        }
        defaults?.Apply(material, BuiltInTexture);
        return material;
    }

    private IFilterTexture? BuiltInTexture(string name)
    {
        byte[]? pixel = name switch
        {
            "white" or "" => [255, 255, 255, 255],
            "black" => [0, 0, 0, 255],
            "gray" or "grey" => [128, 128, 128, 255],
            "bump" => [128, 128, 255, 128],
            "red" => [255, 0, 0, 255],
            _ => null,
        };
        if (pixel is null)
            return null;
        string key = "__unityDefault/" + name;
        if (!_textures.TryGetValue(key, out Texture? texture))
        {
            using var state = new SavedState();
            texture = new Texture(ImageDecoder.Upload(new ImageDecoder.RgbaImage(1, 1, pixel),
                generateMipmaps: false), 1, 1);
            _textures.Add(key, texture);
        }
        return texture;
    }

    public void ReleaseMaterial(IFilterMaterial material)
    {
        // The material only owns its uniform values. Programs and textures are shared by the backend.
    }

    internal IFilterTexture FlatDepthTexture => BuiltInTexture("white")!;

    internal int TextureHandle(string name)
    {
        try
        {
            return ((Texture)LoadTexture(name)).Handle;
        }
        catch (FilterResourceException error)
        {
            Report(error.Message);
            return 0;
        }
    }

    public IFilterTarget RentTarget(int width, int height, int depthBits)
        => RentTarget(width, height, depthBits, halfFloat: false);

    public IFilterTarget RentTargetLike(IFilterTarget source)
    {
        var target = (Target)source;
        return RentTarget(target.Width, target.Height, target.Depth, target.HalfFloat);
    }

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
            throw new InvalidFilterTargetSizeException($"Invalid filter target size {width}×{height}");
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
        if (pool.Count < 6)
        {
            pool.Push(owned);
            _pooledBytes += TargetBytes(owned);
            const long limit = 192L * 1024 * 1024;
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
        Blit((Target)source, (Target)destination, (Material?)material, pass);

    private void Blit(Target input, Target output, Material? parameters, int pass,
        (Vector4 Multiply, Vector4 Add)? modColor = null, Program? custom = null, ModShaderInputs? modInputs = null)
    {
        if (input.Framebuffer == output.Framebuffer)
            throw new InvalidOperationException("Filter source and destination must be distinct.");

        Program program = custom ?? (modColor is not null ? _modColor ??= Link(ModColorFragment)
            : parameters is null ? _copy ??= Link(CopyFragment) :
            GetProgram(parameters.Shader.Name, Math.Max(0, pass)));
        Bind(output);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.ScissorTest);
        GL.ColorMask(true, true, true, true);
        GL.UseProgram(program.Handle);
        GL.BindVertexArray(program.VertexArray == 0 ? _vao : program.VertexArray);
        if (program.ColorAttribute >= 0)
            GL.VertexAttrib4(program.ColorAttribute, 1f, 1f, 1f, 1f);
        BindTexture(program, "_MainTex", input.Handle, 0);
        SetTargetSampling(input.Sampling);
        SetVector(program, "_MainTex_ST", new Vector4(1, 1, 0, 0));
        SetVector(program, "_MainTex_TexelSize", new Vector4(1f / input.Width, 1f / input.Height, input.Width, input.Height));
        // Unity's camera dimensions stay fixed across reduced-resolution Graphics.Blit passes.
        SetVector(program, "_ScreenParams", parameters is not null && _cameraScreenParams is { } cameraScreen
            ? cameraScreen : ScreenParams(output.Width, output.Height));
        float seconds = (float)_time;
        SetVector(program, "_Time", new Vector4(seconds / 20f, seconds, seconds * 2, seconds * 3));
        if (modColor is { } color)
        {
            SetVector(program, "_Multiply", color.Multiply);
            SetVector(program, "_Add", color.Add);
        }
        if (modInputs is { } inputs)
        {
            SetVector(program, "_ModParameters", inputs.Parameters);
            program.SetFloat("_ModTime", inputs.Time);
            for (int index = 0; index < inputs.Textures.Count; index++)
                BindTexture(program, $"_ModInput{index}", ((Target)inputs.Textures[index]).Handle, index + 1);
            program.SetInt("_ModDepthAvailable", inputs.DepthTexture == 0 ? 0 : 1);
            program.SetInt("_ModHistoryValid", inputs.History is null ? 0 : 1);
            BindTexture(program, "_ModDepth", inputs.DepthTexture != 0 ? inputs.DepthTexture : input.Handle, 5);
            BindTexture(program, "_ModHistory", inputs.History?.Handle ?? input.Handle, 6);
        }
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
                if (unit >= FilterTextureUnits)
                    throw new FilterResourceException("Too many texture bindings for " + parameters.Shader.Name);
                BindTexture(program, name, handle, unit++);
                if (value is Target targetTexture)
                    SetTargetSampling(targetTexture.Sampling);
            }
        }

        if (program.VertexArray == 0)
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        else
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    internal void ApplyModColor(Vector4 multiply, Vector4 add)
        => ApplyModPass((multiply, add), null, null, auxiliary: false, history: null);

    internal IFilterTarget? ApplyModShader(string id, string source, Vector4 parameters, double time,
        IReadOnlyList<IFilterTarget> inputs, bool auxiliary, int depthTexture, bool useHistory)
    {
        if (!_modShaders.TryGetValue(source, out Program? program))
        {
            string header = """
                #version 410 core
                in vec2 vs_TEXCOORD0;
                out vec4 color;
                uniform sampler2D _MainTex;
                uniform sampler2D _ModInput0, _ModInput1, _ModInput2, _ModInput3;
                uniform sampler2D _ModDepth, _ModHistory;
                uniform int _ModDepthAvailable, _ModHistoryValid;
                uniform vec4 _ModParameters, _ScreenParams, _MainTex_TexelSize;
                uniform float _ModTime;
                vec4 readColor(vec2 uv) { return texture(_MainTex, uv); }
                float readDepth(vec2 uv) { return _ModDepthAvailable != 0 ? texture(_ModDepth, uv).r : 1.0; }
                vec4 readHistory(vec2 uv) { return _ModHistoryValid != 0 ? texture(_ModHistory, uv) : readColor(uv); }
                """;
            program = Link(header + "\n" + source + "\nvoid main() { color = modEffect(vs_TEXCOORD0, readColor(vs_TEXCOORD0)); }");
            _modShaders.Add(source, program);
        }
        ModHistory? history = null;
        if (useHistory)
        {
            if (!_modHistory.TryGetValue(id, out history)) _modHistory.Add(id, history = new());
            if (time != history.Time)
            {
                if (history.Previous is { } previous) ReleaseTarget(previous);
                history.Previous = history.Current;
                history.Current = null;
                history.Time = time;
            }
        }

        using var binding = new ModAuxiliaryBindings();
        return ApplyModPass(null, program, new(parameters, (float)time, inputs, depthTexture, history?.Previous),
            auxiliary, history);
    }

    private IFilterTarget? ApplyModPass((Vector4 Multiply, Vector4 Add)? color,
        Program? program, ModShaderInputs? inputs, bool auxiliary, ModHistory? history)
    {
        using var state = new SavedState();
        if (state.Width <= 0 || state.Height <= 0) return null;
        TrimPool(state.Width, state.Height);
        var source = (Target)RentTarget(state.Width, state.Height, 0);
        Target? temporary = null;
        int readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        try
        {
            Bind(source);
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, state.Framebuffer);
            GL.BlitFramebuffer(state.X, state.Y, state.X + state.Width, state.Y + state.Height,
                0, 0, source.Width, source.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            if (auxiliary)
                temporary = (Target)RentTarget(state.Width, state.Height, 0);
            Target destination = temporary ?? new(state.Framebuffer, 0, state.Width, state.Height, X: state.X, Y: state.Y);
            Blit(source, destination, null, -1, color, program, inputs);
            if (history is not null)
            {
                var saved = history.Current ??= (Target)RentTarget(state.Width, state.Height, 0);
                Bind(saved);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, destination.Framebuffer);
                GL.BlitFramebuffer(destination.X, destination.Y, destination.X + destination.Width, destination.Y + destination.Height,
                    0, 0, saved.Width, saved.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            }
            return temporary;
        }
        catch { if (temporary is not null) ReleaseTarget(temporary); throw; }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
            ReleaseTarget(source);
        }
    }

    internal void ClearModShaders()
    {
        ResetModHistory();
        foreach (Program program in _modShaders.Values) GL.DeleteProgram(program.Handle);
        _modShaders.Clear();
    }

    internal void ResetModHistory()
    {
        foreach (var history in _modHistory.Values)
        {
            if (history.Previous is { } previous) ReleaseTarget(previous);
            if (history.Current is { } current) ReleaseTarget(current);
        }
        _modHistory.Clear();
    }

    internal void DropModHistory(string id)
    {
        if (!_modHistory.Remove(id, out var history)) return;
        if (history.Previous is { } previous) ReleaseTarget(previous);
        if (history.Current is { } current) ReleaseTarget(current);
    }

    private unsafe struct ModAuxiliaryBindings : IDisposable
    {
        private readonly int _active;
        private fixed int _textures[4];
        private fixed int _samplers[4];
        public ModAuxiliaryBindings()
        {
            this = default;
            _active = GL.GetInteger(GetPName.ActiveTexture);
            for (int i = 0; i < 4; i++)
            {
                GL.ActiveTexture(TextureUnit.Texture3 + i);
                _textures[i] = GL.GetInteger(GetPName.TextureBinding2D);
                _samplers[i] = GL.GetInteger(GetPName.SamplerBinding);
                GL.BindSampler(3 + i, 0);
            }
            GL.ActiveTexture((TextureUnit)_active);
        }

        public void Dispose()
        {
            for (int i = 0; i < 4; i++)
            {
                GL.ActiveTexture(TextureUnit.Texture3 + i);
                GL.BindTexture(TextureTarget.Texture2D, _textures[i]);
                GL.BindSampler(3 + i, _samplers[i]);
            }
            GL.ActiveTexture((TextureUnit)_active);
        }
    }

    private static void SetTargetSampling(FilterSampling sampling)
    {
        TextureMinFilter min = sampling == FilterSampling.Point ? TextureMinFilter.Nearest : TextureMinFilter.Linear;
        TextureMagFilter mag = sampling == FilterSampling.Point ? TextureMagFilter.Nearest : TextureMagFilter.Linear;
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)mag);
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

    private static Vector4 ScreenParams(int width, int height) =>
        new(width, height, 1f + 1f / width, 1f + 1f / height);

    internal static int[] ShaderPasses(string shader) => shader switch
    {
        VideoBloomPass.ShaderName or "CameraFilterPack/Glow_Glow" or "CameraFilterPack/Glow_Glow_Color" => [0, 1, 2, 3],
        "CameraFilterPack/BlurTiltShift" or "CameraFilterPack/BlurTiltShift_Hole" or "CameraFilterPack/BlurTiltShift_V" => [0, 1, 2],
        CameraMotionBlurFilter.ShaderName => [0, 2, 3, 4],
        _ => [0],
    };

    public void Prewarm(string shader, int pass)
    {
        if (_programs.ContainsKey((shader, pass)))
            return;
        using var state = new SavedState();
        using var extraTextures = shader == CameraMotionBlurFilter.ShaderName
            ? new CameraMotionBlurFilter.AdditionalTextureBindings() : default;
        int initial = _borrowed.Count;
        try
        {
            var program = GetProgram(shader, pass);
            var material = (Material)CreateMaterial(FindShader(shader));
            // Some drivers specialize the linked program on its first draw. Scratch
            // targets exercise that path without advancing any live filter component.
            bool halfOutput = shader == VideoBloomPass.ShaderName && pass != 3 ||
                shader == CameraMotionBlurFilter.ShaderName && pass != 4;
            bool halfInput = shader == VideoBloomPass.ShaderName && pass != 3 ||
                shader == CameraMotionBlurFilter.ShaderName && pass is 2 or 3;
            var source = (Target)RentTarget(8, 8, 0, halfInput);
            var destination = (Target)RentTarget(8, 8, 0, halfOutput);
            GL.GetProgram(program.Handle, GetProgramParameterName.ActiveUniforms, out int uniforms);
            for (int index = 0; index < uniforms; index++)
            {
                string sampler = GL.GetActiveUniform(program.Handle, index, out int count, out ActiveUniformType type);
                if (type != ActiveUniformType.Sampler2D || count != 1 || sampler == "_MainTex" ||
                    material.Textures.ContainsKey(sampler))
                    continue;
                bool halfSampler = shader == VideoBloomPass.ShaderName ||
                    shader == CameraMotionBlurFilter.ShaderName && sampler is "_VelTex" or "_NeighbourMaxTex";
                var sample = (Target)RentTarget(8, 8, 0, halfSampler);
                ClearWarmTarget(sample);
                material.SetTexture(sampler, sample);
            }
            ClearWarmTarget(source);
            Blit(source, destination, material, pass);
        }
        catch (Exception error) when (error is FilterResourceException or IOException or
            ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // The live draw path handles and reports unavailable shader resources.
        }
        finally
        {
            ReleaseSince(initial);
        }
    }

    public void PrewarmTexture(string name)
    {
        if (_textures.ContainsKey(name))
            return;
        using var state = new SavedState();
        TextureHandle(name);
    }

    public void PrewarmTargets(int width, int height, int cameras, bool bloom, bool motionBlur)
    {
        if (width <= 0 || height <= 0)
            return;
        using var state = new SavedState();
        int initial = _borrowed.Count;
        try
        {
            TrimPool(width, height);
            for (int i = 0; i < cameras * 3; i++)
                RentTarget(width, height, 0);
            if (bloom)
            {
                float ratio = 198f / height;
                int bloomWidth = Math.Max(1, (int)(ratio * width));
                for (int i = 0; i < 4; i++)
                    RentHalfTarget(bloomWidth, 198);
                if (ratio <= .99f)
                    RentHalfTarget(bloomWidth * 2, 396);
            }
            if (motionBlur)
            {
                RentHalfTarget(width, height);
                RentHalfTarget((width + 7) / 8, (height + 7) / 8);
                RentHalfTarget((width + 7) / 8, (height + 7) / 8);
            }
            for (int index = initial; index < _borrowed.Count; index++)
                ClearWarmTarget(_borrowed[index]);
            if (cameras > 0)
                Blit(_borrowed[initial], _borrowed[initial + 1]);
        }
        catch (Exception error) when (error is FilterResourceException or InvalidFilterTargetSizeException)
        {
            Report(error.Message);
        }
        finally
        {
            ReleaseSince(initial);
        }
    }

    private static void ClearWarmTarget(Target target)
    {
        Bind(target);
        GL.Disable(EnableCap.ScissorTest);
        GL.ColorMask(true, true, true, true);
        GL.Clear(ClearBufferMask.ColorBufferBit);
    }

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
                string? fragment = ShaderLibrary.TryLoad(asset + ".frag");
                program = fragment is not null ? Link(FilterShaderCompatibility.AdaptFragment(shader, fragment)) :
                    LinkUnityFullscreen(shader, ShaderLibrary.Load(asset + ".glsl"));
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

    private Program LinkUnityFullscreen(string shader, string original)
    {
        int handle = SpriteRenderer.LinkSources(UnityShaderAdapter.ImportExportedVertex(original),
            FilterShaderCompatibility.AdaptFragment(shader, UnityShaderAdapter.ImportFullscreenFragment(original),
                original.StartsWith(FilterShaderCompatibility.HlslccDxbcHeader, StringComparison.Ordinal)));
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
        int color = GL.GetAttribLocation(handle, "in_COLOR0");
        if (color >= 0)
        {
            GL.DisableVertexAttribArray(color);
            GL.VertexAttrib4(color, 1f, 1f, 1f, 1f);
        }
        GL.UseProgram(handle);
        var program = new Program(handle, vertexArray, color);
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

    public void ObserveCameraFrame(double time)
    {
        GL.GetInteger(GetPName.Viewport, _cameraViewport);
        ObserveCameraFrame(time, _cameraViewport[2], _cameraViewport[3]);
    }

    private void ObserveCameraFrame(double time, int width, int height)
    {
        if (_cameraHistory.Observe(time, width, height) is { } discarded)
            DeleteTarget(discarded);
        if (_backgroundHistory.Observe(time, width, height) is { } background)
            DeleteTarget(background);
    }

    public void ResetCameraHistory()
    {
        if (_cameraHistory.Clear() is { } history)
            DeleteTarget(history);
        if (_backgroundHistory.Clear() is { } background)
            DeleteTarget(background);
    }

    public unsafe void Draw(double time, double shaderTime, float delta, IReadOnlyList<FilterTimeline.Entry> entries,
        Action drawScene, bool background = false, Vector4? clearSource = null)
    {
        using var state = new SavedState();
        Target screen = new(state.Framebuffer, 0, state.Width, state.Height, X: state.X, Y: state.Y);
        ObserveCameraFrame(time, screen.Width, screen.Height);
        if (screen.Width <= 0 || screen.Height <= 0)
            return;

        var history = background ? _backgroundHistory : _cameraHistory;
        _time = shaderTime;
        DeltaTime = delta;
        ScreenWidth = screen.Width;
        ScreenHeight = screen.Height;
        Vector4? previousScreenParams = _cameraScreenParams;
        _cameraScreenParams = ScreenParams(screen.Width, screen.Height);
        int rentedBefore = _borrowed.Count;
        try
        {
            TrimPool(screen.Width, screen.Height);
            Target first = (Target)RentTarget(screen.Width, screen.Height, 0);
            Target? second = null;
            Bind(first);
            GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);
            if (clearSource is { } color)
            {
                float* rgba = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
                GL.ClearBuffer(ClearBuffer.Color, 0, rgba);
            }
            else
            {
                // Preserve preceding cameras and history already present in the viewport.
                int readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, screen.Framebuffer);
                GL.BlitFramebuffer(screen.X, screen.Y, screen.X + screen.Width, screen.Y + screen.Height,
                    0, 0, first.Width, first.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
            }
            drawScene();
            Target input = first;
            bool succeeded = true;
            for (int index = 0; index < entries.Count; index++)
            {
                Target output = ReferenceEquals(input, first)
                    ? second ??= (Target)RentTarget(screen.Width, screen.Height, 0) : first;
                succeeded &= RenderEntry(entries[index], input, output, history);
                input = output;
            }
            if (entries.Count > 0 && succeeded)
            {
                // Transfer the finished target instead of copying a full frame into history.
                // It stays outside the temporary pool until a later successful frame replaces it.
                Target? previous = history.Output;
                _borrowed.Remove(input);
                history.Remember(input);
                if (previous is not null)
                {
                    _borrowed.Add(previous);
                    ReleaseTarget(previous);
                }
            }
            Blit(input, screen);
        }
        finally
        {
            _cameraScreenParams = previousScreenParams;
            ReleaseSince(rentedBefore);
        }
    }

    private bool RenderEntry(FilterTimeline.Entry entry, Target input, Target output, CameraFilterOutputHistory<Target>? history = null)
    {
        int initial = _borrowed.Count;
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            entry.Filter.Render(input, output);
            return true;
        }
        catch (Exception error) when (error is FilterResourceException or InvalidFilterTargetSizeException or ArithmeticException)
        {
            // Build the stack trace only once for repeated failures in a broken effect.
            if (_reported.Add(entry.Name + ": " + error.Message))
                Console.Error.WriteLine("ADOFAI filter: " + entry.Name + ": " + error);
            Blit(history?.Fallback(error) ?? input, output);
            return false;
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
        ResetModHistory();
        ResetCameraHistory();
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
        if (_modColor is not null)
            GL.DeleteProgram(_modColor.Handle);
        ClearModShaders();
        GL.DeleteVertexArray(_vao);
        if (_unityQuad != 0)
            GL.DeleteBuffer(_unityQuad);
    }

}

internal sealed class FilterResourceException(string message) : Exception(message);
