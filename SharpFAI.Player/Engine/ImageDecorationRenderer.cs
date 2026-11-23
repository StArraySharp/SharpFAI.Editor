using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust.Graphics;
using Thrust.Scene;

namespace SharpFAI.Player.Engine;

internal sealed class ImageDecorationRenderer : IDisposable
{
    private readonly record struct VisibleDecoration(ImageDecorationTimeline.Item Item,
        ImageDecorationTimeline.Pose Pose, Matrix4 Clip)
    {
        public int ParticleOrder { get; init; }
    }

    private readonly List<ParticleDecorationTimeline.Sample> _particleSamples = [];

    private readonly record struct SpriteInstance(Matrix4 Clip, Vector4 Tint, Vector4 Settings, float TextureSlot);

    // OpenGL 3.3 guarantees 16 fragment texture units. Units 0–2 are used by
    // the scalar, framebuffer blend and mask paths.
    private const int FirstBatchTextureUnit = 3;
    private const int BatchTextureCount = 13;

    private static readonly Comparer<VisibleDecoration> StencilOrder = Comparer<VisibleDecoration>.Create(
        static (left, right) => left.Item.Index.CompareTo(right.Item.Index));

    private const string SpriteVertex = """
        #version 330 core
        uniform mat4 _Clip;
        uniform vec2 _ViewportSize;
        uniform int _SmoothEdges;
        layout(location = 0) in vec2 _Corner;
        out vec2 spriteUv;
        flat out vec2 pixelFootprint;
        void main()
        {
            pixelFootprint = vec2(0.0);
            if (_SmoothEdges != 0)
            {
                // Project a one-pixel footprint into the quad's UV axes. Expand the
                // rasterized quad so pixels whose centers miss the edge can still contribute.
                vec2 x = _Clip[0].xy * (_ViewportSize * 0.5);
                vec2 y = _Clip[1].xy * (_ViewportSize * 0.5);
                float area = abs(x.x * y.y - x.y * y.x);
                if (area > 1e-8)
                    pixelFootprint = vec2(abs(y.x) + abs(y.y), abs(x.x) + abs(x.y)) / area;
            }
            vec2 corner = _Corner * (1.0 + pixelFootprint);
            gl_Position = _Clip * vec4(corner, 0.0, 1.0);
            spriteUv = corner + 0.5;
        }
        """;

    private const string SpriteFragmentHeader = """
        #version 330 core
        uniform sampler2D _MainTex;
        uniform sampler2D _MaskTex;
        uniform vec2 _ViewportSize;
        uniform vec4 _Color;
        uniform vec3 _Repeat;
        uniform int _MaskMode;
        in vec2 spriteUv;
        flat in vec2 pixelFootprint;
        out vec4 fragColor;

        vec4 SampleSprite()
        {
            vec2 uv = spriteUv * _Repeat.xy;
            vec2 sampleUv = clamp(spriteUv, 0.0, 1.0) * _Repeat.xy;
            if (_Repeat.z != 0.0)
            {
                bvec2 tiled = notEqual(_Repeat.xy, vec2(1.0));
                sampleUv = mix(sampleUv, fract(sampleUv), tiled);
            }
            // Clamping the expanded border must not change mip selection.
            return textureGrad(_MainTex, sampleUv, dFdx(uv), dFdy(uv));
        }

        float EdgeCoverage()
        {
            if (pixelFootprint.x <= 0.0 || pixelFootprint.y <= 0.0)
                return 1.0;
            vec2 lo = max(spriteUv - 0.5 * pixelFootprint, 0.0);
            vec2 hi = min(spriteUv + 0.5 * pixelFootprint, 1.0);
            vec2 coverage = clamp((hi - lo) / pixelFootprint, 0.0, 1.0);
            return coverage.x * coverage.y;
        }

        float MaskCoverage()
        {
            if (_MaskMode == 0)
                return 1.0;
            float alpha = texture(_MaskTex, gl_FragCoord.xy / _ViewportSize).a;
            return _MaskMode == 1 ? alpha : 1.0 - alpha;
        }
        """;

    private const string MaskFragment = SpriteFragmentHeader + "\n" + """
        void main()
        {
            vec4 c = SampleSprite();
            float coverage = EdgeCoverage();
            // Keep the legacy SpriteMask alpha test independent of decoration opacity.
            if (_MaskMode == 3)
            {
                if (c.a <= 0.5)
                    discard;
                fragColor = vec4(coverage);
                return;
            }
            c *= _Color;
            c.a *= coverage * MaskCoverage();
            c.rgb *= c.a;
            fragColor = c;
        }
        """;

    private const string BlendFragmentSource = SpriteFragmentHeader + "\n" + """
        uniform sampler2D _GrabTex;
        uniform vec4 _GrabViewport;
        uniform int _BlendMode;

        vec3 BlendColors(vec3 base, vec3 top, int mode)
        {
            if (mode == 1) return 1.0 - (1.0 - base) * (1.0 - top);
            if (mode == 2) return base + top;
            if (mode == 3) return mix(2.0 * base * top, 1.0 - 2.0 * (1.0 - base) * (1.0 - top), step(0.5, base));
            if (mode == 4) return (1.0 - base) * base * top + base * (1.0 - (1.0 - base) * (1.0 - top));
            if (mode == 5) return abs(base - top);
            if (mode == 6) return base * top;
            return top;
        }

        void main()
        {
            vec4 c = SampleSprite() * _Color;
            c.a *= EdgeCoverage() * MaskCoverage();
            vec3 base = texture(_GrabTex, (gl_FragCoord.xy - _GrabViewport.xy) / _GrabViewport.zw).rgb;
            c.rgb = BlendColors(base, c.rgb, _BlendMode);
            c.rgb *= c.a;
            fragColor = c;
        }
        """;

    private readonly int _program;
    private readonly int _vao;
    private readonly int _vbo;
    private readonly int _blendProgram;
    private readonly SpriteUniforms _sprite;
    private readonly SpriteUniforms _blendSprite;
    private readonly int _blendGrabViewport;
    private readonly int _blendMode;
    private readonly int _batchProgram;
    private readonly int _batchVao;
    private readonly int _batchViewport;
    private readonly int _batchMaskMode;
    private readonly GlStreamBuffer _batchBuffer = new();
    private readonly SpriteInstance[] _batch = new SpriteInstance[1024];
    private readonly int[] _batchTextures = new int[BatchTextureCount];
    private int _grabTexture;
    private int _grabWidth;
    private int _grabHeight;
    private readonly List<int> _maskTargets = [];
    private int _maskFramebuffer;
    private int _maskWidth;
    private int _maskHeight;
    private readonly Dictionary<int, int> _maskTextures = [];
    private readonly Dictionary<long, int> _legacyMaskTextures = [];
    private readonly List<VisibleDecoration> _legacyStencils = [];
    private readonly Dictionary<string, List<VisibleDecoration>> _taggedStencils = new(StringComparer.Ordinal);
    private readonly List<(List<VisibleDecoration> Stencils, int Texture)> _maskGroups = [];
    private readonly List<VisibleDecoration> _matchingStencils = [];
    private readonly Dictionary<string, int> _singleTagTextures = new(StringComparer.Ordinal);
    private readonly Stack<List<VisibleDecoration>> _stencilLists = [];
    private int _legacyGlobalMask;
    private readonly Dictionary<DecorationTextureStream.Key, int> _textures = [];
    private readonly DecorationTextureStream _animationTextures = new();
    private readonly DecorationTextureStream _staticTextures = new(1280L * 1024 * 1024, preferReuse: false);
    private long _residentStaticBytes;
    private ImageDecorationTimeline? _animationTimeline;
    private double _animationTime = double.NegativeInfinity;
    private int _nextAnimationUpload;
    private int _nextStaticUpload;
    private readonly List<VisibleDecoration> _visible = [];
    private readonly List<VisibleDecoration> _synced = [];
    private bool _hasVisibleMask;
    private readonly DecorationFrameState _frameState = new();
    private readonly Dictionary<int, Matrix4> _editorQuads = [];

    // Keep the scalar path available for render regression checks and profiling.
    internal bool UseInstancing { get; set; } = true;

    private sealed class SpriteUniforms(int program)
    {
        public readonly int MaskMode = GL.GetUniformLocation(program, "_MaskMode");
        private readonly int _clip = GL.GetUniformLocation(program, "_Clip");
        private readonly int _color = GL.GetUniformLocation(program, "_Color");
        private readonly int _repeat = GL.GetUniformLocation(program, "_Repeat");
        private readonly int _smoothEdges = GL.GetUniformLocation(program, "_SmoothEdges");
        private readonly int _viewport = GL.GetUniformLocation(program, "_ViewportSize");

        public void Set(Matrix4 clip, Vector4 tint, Vector2 repeat, bool smooth, Vector2 viewport, bool wrapInShader = false)
        {
            GL.UniformMatrix4(_clip, false, ref clip);
            GL.Uniform4(_color, tint);
            GL.Uniform3(_repeat, repeat.X, repeat.Y, wrapInShader && repeat != Vector2.One ? 1f : 0f);
            GL.Uniform1(_smoothEdges, smooth ? 1 : 0);
            GL.Uniform2(_viewport, viewport);
        }
    }

    public ImageDecorationRenderer()
    {
        _program = SpriteRenderer.LinkSources(SpriteVertex, MaskFragment);
        _blendProgram = SpriteRenderer.LinkSources(SpriteVertex, BlendFragmentSource);
        _sprite = new SpriteUniforms(_program);
        _blendSprite = new SpriteUniforms(_blendProgram);
        _blendGrabViewport = GL.GetUniformLocation(_blendProgram, "_GrabViewport");
        _blendMode = GL.GetUniformLocation(_blendProgram, "_BlendMode");
        GL.UseProgram(_program);
        GL.Uniform1(GL.GetUniformLocation(_program, "_MaskTex"), 2);
        GL.UseProgram(_blendProgram);
        GL.Uniform1(GL.GetUniformLocation(_blendProgram, "_MaskTex"), 2);
        GL.Uniform1(GL.GetUniformLocation(_blendProgram, "_GrabTex"), 1);
        GL.UseProgram(0);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        float[] vertices = [-.5f, -.5f, .5f, -.5f, -.5f, .5f, .5f, .5f];
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

        // Use the same shader arithmetic for batched sprites; only the source of
        // per-sprite values changes from uniforms to instance attributes.
        string batchVertex = SpriteVertex
            .Replace("uniform mat4 _Clip;", "layout(location = 1) in mat4 _Clip;")
            .Replace("uniform int _SmoothEdges;", """
                layout(location = 5) in vec4 _Tint;
                layout(location = 6) in vec4 _Settings;
                layout(location = 7) in float _TextureSlot;
                flat out vec4 _Color;
                flat out vec3 _Repeat;
                flat out int textureSlot;
                """)
            .Replace("_SmoothEdges != 0", "_Settings.w != 0.0")
            .Replace("pixelFootprint = vec2(0.0);", "_Color = _Tint; _Repeat = _Settings.xyz; textureSlot = int(_TextureSlot);\npixelFootprint = vec2(0.0);");
        // GLSL 330 requires constant sampler indices. Explicit gradients preserve
        // sampling when neighboring fragments select different textures.
        string cases = string.Join("\n", Enumerable.Range(0, BatchTextureCount - 1)
            .Select(slot => $"case {slot}: return textureGrad(_MainTex[{slot}], sampleUv, dx, dy);"));
        string batchFragment = MaskFragment
            .Replace("uniform sampler2D _MainTex;", $"uniform sampler2D _MainTex[{BatchTextureCount}];\nflat in int textureSlot;")
            .Replace("return textureGrad(_MainTex, sampleUv, dFdx(uv), dFdy(uv));", $$"""
                vec2 dx = dFdx(uv);
                vec2 dy = dFdy(uv);
                switch (textureSlot)
                {
                    {{cases}}
                    default: return textureGrad(_MainTex[{{BatchTextureCount - 1}}], sampleUv, dx, dy);
                }
                """)
            .Replace("uniform vec4 _Color;", "flat in vec4 _Color;")
            .Replace("uniform vec3 _Repeat;", "flat in vec3 _Repeat;");
        _batchProgram = SpriteRenderer.LinkSources(batchVertex, batchFragment);
        _batchViewport = GL.GetUniformLocation(_batchProgram, "_ViewportSize");
        _batchMaskMode = GL.GetUniformLocation(_batchProgram, "_MaskMode");
        GL.UseProgram(_batchProgram);
        for (int slot = 0; slot < BatchTextureCount; slot++)
            GL.Uniform1(GL.GetUniformLocation(_batchProgram, $"_MainTex[{slot}]"), FirstBatchTextureUnit + slot);
        GL.Uniform1(GL.GetUniformLocation(_batchProgram, "_MaskTex"), 2);
        GL.UseProgram(0);
        _batchVao = GL.GenVertexArray();
        GL.BindVertexArray(_batchVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _batchBuffer.Handle);
        const int stride = 25 * sizeof(float);
        for (int attribute = 0; attribute < 6; attribute++)
        {
            int location = attribute + 1;
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false, stride, attribute * 4 * sizeof(float));
            GL.VertexAttribDivisor(location, 1);
        }
        GL.EnableVertexAttribArray(7);
        GL.VertexAttribPointer(7, 1, VertexAttribPointerType.Float, false, stride, 24 * sizeof(float));
        GL.VertexAttribDivisor(7, 1);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    public void Prepare(ImageDecorationTimeline timeline, double time, Camera2D camera, float tileSize,
        float authoredZoom, Func<int, bool, (Vector2 Center, Vector2 Scale, float Rotation)> floor,
        Func<string, Vector2> planet, bool collectEditorQuads = false, Func<int, float>? floorOpacity = null)
    {
        _visible.Clear();
        _synced.Clear();
        _hasVisibleMask = false;
        _editorQuads.Clear();
        _animationTextures.BeginFrame(time);
        _staticTextures.BeginFrame(time);
        Matrix4 view = camera.ViewProjection;
        foreach (var (item, authoredPose, pixels) in _frameState.Update(timeline, time, collectEditorQuads))
        {
            var pose = item.StickToFloor && floorOpacity is not null
                ? authoredPose with { Opacity = authoredPose.Opacity * floorOpacity(item.Floor) } : authoredPose;
            if (item.IsPlayerBubble && !collectEditorQuads)
                continue;
            if (pose.Image == ObjectDecorationArt.PlanetKey)
                pose = pose with { Image = ObjectDecorationArt.PlanetImageAt(time) };
            Matrix4 model = ModelFor(pixels, item, pose, camera, tileSize, authoredZoom, floor, planet);
            bool screen = IsScreenRelative(pose);
            Matrix4 clip = screen ? model : model * view;
            if (item.Particles is { } particles)
            {
                if (collectEditorQuads && item.DecorationIndex >= 0 && !IsOutsideViewport(clip))
                    _editorQuads[item.DecorationIndex] = clip;
                particles.Collect(time, _particleSamples);
                Matrix4 axes = ModelFor(Vector2.One, item, pose with { Scale = Vector2.One }, camera,
                    tileSize, authoredZoom, floor, planet);
                for (int i = 0; i < _particleSamples.Count; i++)
                {
                    var particle = _particleSamples[i];
                    if (particle.Size <= 0 || particle.Tint.W <= 0)
                        continue;
                    var anchor = particle.World ? item.At(particle.Birth) : pose;
                    bool anchorScreen = IsScreenRelative(anchor);
                    Matrix4 origin = particle.World ? ModelFor(pixels, item, anchor, camera, tileSize, authoredZoom, floor, planet) : model;
                    Matrix4 basis = particle.World ? ModelFor(Vector2.One, item, anchor with { Scale = Vector2.One }, camera,
                        tileSize, authoredZoom, floor, planet) : axes;
                    Vector2 offset = (particle.ShapeOffset * anchor.Scale + particle.Travel) * tileSize;
                    Vector4 center = origin.Row3 + basis.Row0 * offset.X + basis.Row1 * offset.Y;
                    basis.Row3 = center;
                    // Authored size in percent of one Unity unit already equals the diameter in player units.
                    Matrix4 particleModel = Matrix4.CreateScale(particle.Size) *
                        Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(particle.Rotation)) * basis;
                    Matrix4 particleClip = anchorScreen ? particleModel : particleModel * view;
                    if (!IsOutsideViewport(particleClip))
                    {
                        _visible.Add(new(item, pose with { Tint = pose.Tint * particle.Tint }, particleClip) { ParticleOrder = i });
                        _hasVisibleMask |= pose.Masking == DecorationMasking.Mask;
                    }
                }
                continue;
            }
            if (!IsOutsideViewport(clip))
            {
                var decoration = new VisibleDecoration(item, pose, clip);
                _visible.Add(decoration);
                _hasVisibleMask |= pose.Masking == DecorationMasking.Mask;
                if (pose.SyncFloorDepth && pose.Opacity > 0f && pose.Masking != DecorationMasking.Mask && !IsTransparent(pose))
                    _synced.Add(decoration);
                if (collectEditorQuads && item.DecorationIndex >= 0 && pose.Masking != DecorationMasking.Mask)
                    _editorQuads[item.DecorationIndex] = clip;
            }
        }
        // Unity paints in ascending sortingOrder, and the decoration uses sortingOrder = -depth,
        // so depth = -10 (order 10) draws last and lands on top, while depth = 10 sinks below.
        _visible.Sort(static (left, right) =>
        {
            int depth = DepthKey(left.Pose).CompareTo(DepthKey(right.Pose));
            if (depth != 0)
                return depth;
            int item = left.Item.Index.CompareTo(right.Item.Index);
            return item != 0 ? item : left.ParticleOrder.CompareTo(right.ParticleOrder);
        });
        _synced.Sort(static (left, right) =>
        {
            int floor = right.Item.Floor.CompareTo(left.Item.Floor);
            return floor == 0 ? left.Item.Index.CompareTo(right.Item.Index) : floor;
        });
        PrefetchTextures(timeline, time);
    }

    private void PrefetchTextures(ImageDecorationTimeline timeline, double time)
    {
        var animated = timeline.AnimationUploads;
        var statics = timeline.StaticTexturePrefetches;
        const double animationLookahead = .16;
        const double staticLookahead = 12;
        if (!ReferenceEquals(timeline, _animationTimeline) || time < _animationTime || time - _animationTime > animationLookahead)
        {
            _animationTimeline = timeline;
            _nextAnimationUpload = FirstUpcoming(animated, time, static frame => frame.Start);
            _nextStaticUpload = FirstUpcoming(statics, time, static frame => frame.Start);
        }
        _animationTime = time;
        bool animationDue = _nextAnimationUpload < animated.Length && animated[_nextAnimationUpload].Start <= time + animationLookahead;
        bool staticDue = _nextStaticUpload < statics.Length && statics[_nextStaticUpload].Start <= time + staticLookahead;
        if (!animationDue && !staticDue)
            return;

        int active = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        int texture = GL.GetInteger(GetPName.TextureBinding2D);
        try
        {
            while (_nextAnimationUpload < animated.Length && animated[_nextAnimationUpload].Start <= time + animationLookahead)
            {
                var frame = animated[_nextAnimationUpload++];
                if (frame.Start <= time || !timeline.Images.TryGetValue(frame.Image, out var image))
                    continue;
                var (repeatX, repeatY) = _frameState.Repeat(frame.Image);
                var key = new DecorationTextureStream.Key(frame.Image, frame.Smoothing, frame.RepeatX || repeatX, frame.RepeatY || repeatY);
                // At most one new transfer per rendered frame; drawing still resolves cache misses.
                if (_animationTextures.Prefetch(key, image, frame.Start + .05))
                    break;
            }
            while (_nextStaticUpload < statics.Length && statics[_nextStaticUpload].Start <= time + staticLookahead)
            {
                var (key, start) = statics[_nextStaticUpload];
                if (start <= time)
                {
                    _nextStaticUpload++;
                    continue;
                }
                var (repeatX, repeatY) = _frameState.Repeat(key.Image);
                key = key with { RepeatX = key.RepeatX || repeatX, RepeatY = key.RepeatY || repeatY };
                if (_textures.ContainsKey(key))
                {
                    _nextStaticUpload++;
                    continue;
                }
                var image = timeline.Images[key.Image];
                // Start large uploads farther ahead and keep the per-frame transfer
                // small until the texture is close to its first use after a seek.
                int transferBudget = (start - time <= 2 ? 4 : 1) * 1024 * 1024;
                bool transferred = _staticTextures.Prefetch(key, image, start + .05, transferBudget, out bool ready);
                if (ready)
                    _nextStaticUpload++;
                if (transferred || !ready)
                    break;
            }
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.ActiveTexture((TextureUnit)active);
        }
    }

    private static int FirstUpcoming<T>(T[] entries, double time, Func<T, double> start)
    {
        int first = 0, end = entries.Length;
        while (first < end)
        {
            int middle = first + (end - first) / 2;
            if (start(entries[middle]) <= time)
                first = middle + 1;
            else
                end = middle;
        }
        return first;
    }

    internal bool TryGetEditorQuad(int decorationIndex, out Matrix4 clip) =>
        _editorQuads.TryGetValue(decorationIndex, out clip);

    internal Dictionary<int, Matrix4>.KeyCollection VisibleEditorDecorations => _editorQuads.Keys;

    private static int DepthKey(ImageDecorationTimeline.Pose pose) => pose.SyncFloorDepth ? 0 : -pose.Depth;

    private static bool IsTransparent(ImageDecorationTimeline.Pose pose) =>
        pose.Masking != DecorationMasking.Mask && pose.Tint.W == 0f;

    public void DrawLayer(int texture, Matrix4 model, Vector4 tint, Vector2 repeat)
    {
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(_sprite.MaskMode, 0);
        _sprite.Set(model, tint, repeat, false, Vector2.Zero);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        GL.Disable(EnableCap.Blend);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    public int SyncedCount => _synced.Count;
    public int SyncedFloor(int index) => _synced[index].Item.Floor;

    public int DrawSyncedFloor(ImageDecorationTimeline timeline, Camera2D camera, ref int index)
    {
        int first = index;
        int floor = SyncedFloor(first);
        while (index < _synced.Count && SyncedFloor(index) == floor)
            index++;
        return Draw(timeline, false, camera, first, index);
    }

    public int Draw(ImageDecorationTimeline timeline, bool background, Camera2D camera,
        int syncedStart = -1, int syncedEnd = 0)
    {
        if (_visible.Count == 0)
            return 0;

        // The background pass builds masks for both layers before either is drawn.
        Vector2 viewport = new(camera.ViewportWidth, camera.ViewportHeight);
        if (background)
        {
            if (_hasVisibleMask)
                RenderMasks(timeline, viewport);
            else
            {
                _maskTextures.Clear();
                _legacyMaskTextures.Clear();
                _legacyGlobalMask = 0;
            }
        }

        GL.BindVertexArray(_vao);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        int draws = 0;
        int batchCount = 0, textureCount = 0, batchMask = 0, batchMode = 0;
        int boundTextureCount = 0;
        void Flush()
        {
            if (batchCount == 0) return;
            // Isolated sprites are cheaper to submit without a buffer upload.
            if (batchCount == 1)
            {
                GL.UseProgram(_program);
                GL.BindVertexArray(_vao);
                GL.Uniform1(_sprite.MaskMode, batchMode);
                if (batchMask != 0)
                {
                    GL.ActiveTexture(TextureUnit.Texture2);
                    GL.BindTexture(TextureTarget.Texture2D, batchMask);
                }
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _batchTextures[0]);
                var sprite = _batch[0];
                _sprite.Set(sprite.Clip, sprite.Tint, new Vector2(sprite.Settings.X, sprite.Settings.Y),
                    sprite.Settings.W != 0, viewport, sprite.Settings.Z != 0);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }
            else
            {
                GL.UseProgram(_batchProgram);
                GL.BindVertexArray(_batchVao);
                GL.Uniform2(_batchViewport, viewport);
                GL.Uniform1(_batchMaskMode, batchMode);
                GL.ActiveTexture(TextureUnit.Texture2);
                GL.BindTexture(TextureTarget.Texture2D, batchMask);
                for (int slot = 0; slot < textureCount; slot++)
                {
                    GL.ActiveTexture(TextureUnit.Texture0 + FirstBatchTextureUnit + slot);
                    GL.BindTexture(TextureTarget.Texture2D, _batchTextures[slot]);
                }
                boundTextureCount = Math.Max(boundTextureCount, textureCount);
                GL.ActiveTexture(TextureUnit.Texture0);
                _batchBuffer.Upload<SpriteInstance>(_batch.AsSpan(0, batchCount));
                GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, batchCount);
            }
            batchCount = 0;
            textureCount = 0;
            draws++;
        }
        bool synced = syncedStart >= 0;
        var decorations = synced ? _synced : _visible;
        for (int index = synced ? syncedStart : 0; index < (synced ? syncedEnd : decorations.Count); index++)
        {
            var (item, pose, clip) = decorations[index];
            if (!synced && (pose.SyncFloorDepth || (pose.Depth >= 0) != background))
                continue;

            // A decoration with maskingType Mask is a stencil only: the game disables its renderer
            // and drives a SpriteMask instead, so it must never reach the main pass.
            if (pose.Masking == DecorationMasking.Mask || IsTransparent(pose))
                continue;

            // The game resolves the masks a decoration touches before drawing it. A
            // VisibleInsideMask decoration with no matching mask renders nothing (the game clears
            // its render target), while VisibleOutsideMask keeps the artwork unclipped.
            int mask = IsMaskRecipient(pose) ? FindMask(item, pose) : 0;
            if (pose.Masking == DecorationMasking.VisibleInsideMask && mask == 0)
                continue;

            int texture = TextureFor(timeline, pose);
            Vector4 tint = pose.Tint;
            tint.W *= pose.Opacity;
            if (UseInstancing && pose.Blend == DecorationBlendMode.None)
            {
                int mode = mask == 0 ? 0 : pose.Masking == DecorationMasking.VisibleOutsideMask ? 2 : 1;
                if (batchCount == _batch.Length || batchMask != mask || batchMode != mode)
                    Flush();
                int slot = _batchTextures.AsSpan(0, textureCount).IndexOf(texture);
                if (slot < 0)
                {
                    if (textureCount == BatchTextureCount)
                        Flush();
                    slot = textureCount++;
                    _batchTextures[slot] = texture;
                }
                batchMask = mask;
                batchMode = mode;
                _batch[batchCount++] = new SpriteInstance(clip, tint, new Vector4(item.Tile.X, item.Tile.Y,
                    !timeline.LegacyMasks && item.Tile != Vector2.One ? 1f : 0f, pose.Smoothing ? 1f : 0f), slot);
                continue;
            }
            Flush();
            GL.BindVertexArray(_vao);
            if (pose.Blend == DecorationBlendMode.None)
            {
                GL.UseProgram(_program);
                _sprite.Set(clip, tint, item.Tile, pose.Smoothing, viewport, !timeline.LegacyMasks);
                BindMask(_sprite, pose.Masking, mask);
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }
            else
            {
                // The game blends against the framebuffer; copy it first because GLSL 330 has no
                // framebuffer fetch. Each blend decoration needs a fresh copy of the result so far.
                var grabViewport = CopyFramebuffer();
                if (grabViewport.Z <= 0 || grabViewport.W <= 0)
                    continue;
                GL.UseProgram(_blendProgram);
                _blendSprite.Set(clip, tint, item.Tile, pose.Smoothing, viewport, !timeline.LegacyMasks);
                GL.Uniform4(_blendGrabViewport, grabViewport);
                GL.Uniform1(_blendMode, (int)pose.Blend);
                BindMask(_blendSprite, pose.Masking, mask);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, _grabTexture);
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.ActiveTexture(TextureUnit.Texture0);
            }
            draws++;
        }
        Flush();
        for (int slot = 0; slot < boundTextureCount; slot++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + FirstBatchTextureUnit + slot);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Disable(EnableCap.Blend);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        return draws;
    }

    internal static bool IsScreenRelative(ImageDecorationTimeline.Pose pose) =>
        pose.RelativeTo is "Camera" or "CameraAspect";

    private static bool IsMaskRecipient(ImageDecorationTimeline.Pose pose) =>
        pose.Masking is DecorationMasking.VisibleInsideMask or DecorationMasking.VisibleOutsideMask;

    private int FindMask(ImageDecorationTimeline.Item item, ImageDecorationTimeline.Pose pose)
    {
        if (_maskTextures.TryGetValue(item.Index, out int texture))
            return texture;
        return _legacyGlobalMask != 0 ? _legacyGlobalMask :
            _legacyMaskTextures.GetValueOrDefault(DecorationMaskRange.DepthKey(pose.Depth, pose.SyncFloorDepth));
    }

    private static void BindMask(SpriteUniforms sprite, DecorationMasking masking, int mask)
    {
        GL.Uniform1(sprite.MaskMode, mask == 0 ? 0 : masking == DecorationMasking.VisibleOutsideMask ? 2 : 1);
        if (mask == 0)
            return;
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, mask);
        GL.ActiveTexture(TextureUnit.Texture0);
    }

    private void RenderMasks(ImageDecorationTimeline timeline, Vector2 viewport)
    {
        _maskTextures.Clear();
        _legacyMaskTextures.Clear();
        _legacyGlobalMask = 0;

        _legacyStencils.Clear();
        foreach (var list in _taggedStencils.Values)
        {
            list.Clear();
            _stencilLists.Push(list);
        }
        _taggedStencils.Clear();
        foreach (var group in _maskGroups)
        {
            group.Stencils.Clear();
            _stencilLists.Push(group.Stencils);
        }
        _maskGroups.Clear();
        _matchingStencils.Clear();
        _singleTagTextures.Clear();
        var legacyStencils = _legacyStencils;
        var taggedStencils = _taggedStencils;
        foreach (var decoration in _visible)
        {
            var pose = decoration.Pose;
            if (pose.Masking != DecorationMasking.Mask)
                continue;
            if (pose.UsesDepthMask(timeline.LegacyMasks))
            {
                legacyStencils.Add(decoration);
                continue;
            }
            string target = pose.MaskingTarget;
            if (pose.Opacity <= 0 || string.IsNullOrWhiteSpace(target) || target == "NO TAG")
                continue;
            if (!taggedStencils.TryGetValue(target, out var stencils))
                taggedStencils.Add(target, stencils = RentStencilList());
            stencils.Add(decoration);
        }
        if (legacyStencils.Count == 0 && taggedStencils.Count == 0)
            return;

        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        int draw = GL.GetInteger(GetPName.DrawFramebufferBinding);
        if (taggedStencils.Count == 0)
            RenderLegacyMasks(timeline, viewport, legacyStencils);
        else
            RenderTaggedMasks(timeline, viewport, legacyStencils);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
        GL.BindTexture(TextureTarget.Texture2D, previousTexture);
    }

    private void RenderTaggedMasks(ImageDecorationTimeline timeline, Vector2 viewport, List<VisibleDecoration> depthStencils)
    {
        int targetIndex = 0;
        foreach (var receiver in _visible)
        {
            var pose = receiver.Pose;
            if (!IsMaskRecipient(pose))
                continue;
            var tags = receiver.Item.Tags;
            if (depthStencils.Count == 0 && tags.Length == 1)
            {
                string tag = tags[0];
                if (!_taggedStencils.TryGetValue(tag, out var stencils))
                    continue;
                if (!_singleTagTextures.TryGetValue(tag, out int texture))
                    _singleTagTextures.Add(tag, texture = RenderMask(timeline, viewport, stencils, targetIndex++));
                _maskTextures[receiver.Item.Index] = texture;
                continue;
            }

            var matching = _matchingStencils;
            matching.Clear();
            long depth = DecorationMaskRange.DepthKey(pose.Depth, pose.SyncFloorDepth);
            foreach (var stencil in depthStencils)
                if (stencil.Pose.MaskRange.Includes(depth))
                    matching.Add(stencil);
            int depthCount = matching.Count;
            foreach (string tag in tags)
                if (_taggedStencils.TryGetValue(tag, out var stencils))
                    matching.AddRange(stencils);
            if (matching.Count == 0)
                continue;
            matching.Sort(depthCount, matching.Count - depthCount, StencilOrder);

            int group = 0;
            while (group < _maskGroups.Count && !_maskGroups[group].Stencils.SequenceEqual(matching))
                group++;
            if (group == _maskGroups.Count)
            {
                var copy = RentStencilList();
                copy.AddRange(matching);
                _maskGroups.Add((copy, RenderMask(timeline, viewport, matching, targetIndex++, depthCount)));
            }
            _maskTextures[receiver.Item.Index] = _maskGroups[group].Texture;
        }
    }

    private void RenderLegacyMasks(ImageDecorationTimeline timeline, Vector2 viewport, List<VisibleDecoration> stencils)
    {
        if (!stencils.Exists(static stencil => stencil.Pose.MaskRange.Enabled))
        {
            _legacyGlobalMask = RenderMask(timeline, viewport, stencils, 0, stencils.Count);
            return;
        }

        // Build only the sets that visible recipients use. Different depths often share a stencil set.
        var groups = _maskGroups;
        var matching = _matchingStencils;
        foreach (var decoration in _visible)
        {
            var pose = decoration.Pose;
            if (!IsMaskRecipient(pose))
                continue;
            long depth = DecorationMaskRange.DepthKey(pose.Depth, pose.SyncFloorDepth);
            if (_legacyMaskTextures.ContainsKey(depth))
                continue;

            matching.Clear();
            foreach (var stencil in stencils)
            {
                if (stencil.Pose.MaskRange.Includes(depth))
                    matching.Add(stencil);
            }
            if (matching.Count == 0)
            {
                _legacyMaskTextures.Add(depth, 0);
                continue;
            }
            int group = 0;
            while (group < groups.Count && !groups[group].Stencils.SequenceEqual(matching))
                group++;
            if (group == groups.Count)
            {
                var copy = RentStencilList();
                copy.AddRange(matching);
                groups.Add((copy, RenderMask(timeline, viewport, matching, group, matching.Count)));
            }
            _legacyMaskTextures.Add(depth, groups[group].Texture);
        }
    }

    private List<VisibleDecoration> RentStencilList() => _stencilLists.TryPop(out var list) ? list : [];

    private int RenderMask(ImageDecorationTimeline timeline, Vector2 viewport, List<VisibleDecoration> stencils,
        int index, int depthCount = 0)
    {
        int texture = EnsureMaskTarget((int)viewport.X, (int)viewport.Y, index);
        if (texture == 0)
            return 0;

        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _maskFramebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, texture, 0);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        if (depthCount != 0)
            GL.BlendEquation(BlendEquationMode.Max);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.Uniform1(_sprite.MaskMode, depthCount != 0 ? 3 : 0);
        for (int i = 0; i < stencils.Count; i++)
        {
            if (i == depthCount && depthCount != 0)
            {
                GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.Uniform1(_sprite.MaskMode, 0);
            }
            var (item, pose, clip) = stencils[i];
            int spriteTexture = TextureFor(timeline, pose);
            _sprite.Set(clip, new Vector4(1f, 1f, 1f, pose.Opacity), item.Tile, pose.Smoothing, viewport, !timeline.LegacyMasks);
            GL.BindTexture(TextureTarget.Texture2D, spriteTexture);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        if (depthCount == stencils.Count)
            GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.Disable(EnableCap.Blend);
        return texture;
    }

    private int EnsureMaskTarget(int width, int height, int index)
    {
        if (width <= 0 || height <= 0)
            return 0;
        if (width != _maskWidth || height != _maskHeight)
        {
            foreach (int oldTexture in _maskTargets)
                GL.DeleteTexture(oldTexture);
            _maskTargets.Clear();
            _maskWidth = width;
            _maskHeight = height;
        }
        if (index < _maskTargets.Count)
            return _maskTargets[index];

        if (_maskFramebuffer == 0)
            _maskFramebuffer = GL.GenFramebuffer();
        int texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _maskTargets.Add(texture);
        return texture;
    }

    private int TextureFor(ImageDecorationTimeline timeline, ImageDecorationTimeline.Pose pose)
    {
        var (repeatX, repeatY) = _frameState.Repeat(pose.Image);
        var key = new DecorationTextureStream.Key(pose.Image, pose.Smoothing, repeatX, repeatY);
        if (_textures.TryGetValue(key, out int texture))
            return texture;

        var image = timeline.Images[pose.Image];
        if (timeline.AnimationFrames.Contains(pose.Image))
            return _animationTextures.Get(key, image);
        long bytes = image.TextureBytes;
        int handle = _staticTextures.Get(key, image);
        // Keep commonly reused smaller artwork from competing with upcoming large backgrounds.
        const long residentBudget = 640L * 1024 * 1024;
        if (bytes <= 16L * 1024 * 1024 && _residentStaticBytes + bytes <= residentBudget &&
            _staticTextures.TryTake(key, out int resident))
        {
            _textures.Add(key, resident);
            _residentStaticBytes += bytes;
        }
        return handle;
    }

    internal void PrewarmTexture(DecorationTextureStream.Key key, in DecorationImage image)
    {
        if (_textures.ContainsKey(key))
            return;
        _staticTextures.Get(key, image);
        if (_staticTextures.TryTake(key, out int handle))
        {
            _textures.Add(key, handle);
            _residentStaticBytes += image.TextureBytes;
        }
    }

    internal static bool IsOutsideViewport(Matrix4 clip)
    {
        // Orthographic projection of a centered unit quad; these are its axis-aligned extents in clip space.
        float halfWidth = (MathF.Abs(clip.Row0.X) + MathF.Abs(clip.Row1.X)) * .5f;
        float halfHeight = (MathF.Abs(clip.Row0.Y) + MathF.Abs(clip.Row1.Y)) * .5f;
        return MathF.Abs(clip.Row3.X) - halfWidth > 1f || MathF.Abs(clip.Row3.Y) - halfHeight > 1f;
    }

    internal static Matrix4 ModelFor(Vector2 pixels, ImageDecorationTimeline.Item item, ImageDecorationTimeline.Pose pose,
        Camera2D camera, float tileSize, float authoredZoom,
        Func<int, bool, (Vector2 Center, Vector2 Scale, float Rotation)> floor, Func<string, Vector2> planet)
    {
        bool screen = IsScreenRelative(pose);
        Vector2 size = WorldSize(pixels, pose, item, authoredZoom, camera.Zoom);
        float angle = pose.Rotation;
        float lockZoom = item.LockScale ? authoredZoom / camera.Zoom : 1f;
        Vector2 position = pose.Position * (screen ? 0.1f : tileSize);
        var parent = item.StickToFloor || (!screen && pose.RelativeTo == "Tile")
            ? floor(item.Floor, item.StickToFloor) : default;
        if (item.StickToFloor)
        {
            size *= parent.Scale;
            angle += parent.Rotation;
        }
        else if (item.LockRotation)
            angle += camera.Rotation;
        // The crop offset is in sprite pixels, independent of tile size and texture scaling.
        Vector2 pivot = (pose.Pivot * tileSize + pose.CropPivotOffset) * pose.Scale * lockZoom;
        if (item.StickToFloor)
            pivot *= parent.Scale;
        Vector2 parallaxOffset = pose.ParallaxOffset * tileSize * item.ScaleMultiplier * lockZoom;
        if (screen)
        {
            float aspect = camera.ViewportHeight / Math.Max(camera.ViewportWidth, 1f);
            position += pose.Pivot * (tileSize / 1000f);
            if (pose.RelativeTo == "CameraAspect")
                position.X *= aspect;
            var worldOffset = Rotate(parallaxOffset + Rotate(pivot, angle), -camera.Rotation);
            position += worldOffset * new Vector2(2f / camera.WorldViewWidth, 2f / camera.WorldViewHeight);
            angle -= camera.Rotation;
        }
        else
        {
            if (pose.RelativeTo == "Tile")
                position += parent.Center;
            else if (pose.RelativeTo is "RedPlanet" or "BluePlanet" or "GreenPlanet")
                position += planet(pose.RelativeTo);
            else if (item.StickToFloor)
                position += parent.Center - floor(item.Floor, false).Center;

            // Unity's scrParallax.SetTrans:
            //   pos = startPos + (camera.position - posCamAtStart) * multiplier
            //   pos += parallaxOffset * camScaleMultiplier / textureScaleMultiplier
            // scrDecoration.UpdatePosition sets startPos and posCamAtStart to the same
            // value: the decoration's own pivot position, so the camera delta is measured
            // from the unparallaxed base position, not from its floor tile.
            position += (camera.Position - position) * pose.Parallax * .01f;
            // camScaleMultiplier = lockScaleZoom * scaleMultiplier * textureScaleMultiplier,
            // so the texture scale factors cancel and the offset is scaled only by
            // scaleMultiplier (and the lockScale zoom ratio), never by textureScale.
            position += parallaxOffset + Rotate(pivot, angle);
        }

        Matrix4 model = Matrix4.CreateScale(size.X, size.Y, 1f) *
            Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(angle));
        if (screen)
            model *= Matrix4.CreateScale(2f / camera.WorldViewWidth, 2f / camera.WorldViewHeight, 1f);
        // The linear part has homogeneous w=0. Post-multiplying by translation
        // only changes the last row.
        model.Row3 = new Vector4(position.X, position.Y, 0f, 1f);
        return model;
    }

    private static Vector2 Rotate(Vector2 offset, float angle)
    {
        if (offset == Vector2.Zero || angle == 0f)
            return offset;
        var (sin, cos) = MathF.SinCos(MathHelper.DegreesToRadians(angle));
        return new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos);
    }

    private Vector4 CopyFramebuffer()
    {
        Span<int> viewport = stackalloc int[4];
        unsafe { fixed (int* view = viewport) GL.GetInteger(GetPName.Viewport, view); }
        int width = viewport[2];
        int height = viewport[3];
        if (width <= 0 || height <= 0)
            return Vector4.Zero;

        if (_grabTexture == 0)
        {
            _grabTexture = GL.GenTexture();
        }
        int previous = GL.GetInteger(GetPName.TextureBinding2D);
        GL.BindTexture(TextureTarget.Texture2D, _grabTexture);
        if (width != _grabWidth || height != _grabHeight)
        {
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _grabWidth = width;
            _grabHeight = height;
        }
        // Filters bind only the draw framebuffer, but the copy reads from the read framebuffer.
        int previousRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int draw = GL.GetInteger(GetPName.DrawFramebufferBinding);
        if (previousRead != draw)
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, draw);
        GL.CopyTexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, viewport[0], viewport[1], width, height);
        if (previousRead != draw)
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousRead);
        GL.BindTexture(TextureTarget.Texture2D, previous);
        return new Vector4(viewport[0], viewport[1], width, height);
    }

    internal static Vector2 WorldSize(Vector2 pixels, ImageDecorationTimeline.Pose pose,
        ImageDecorationTimeline.Item item, float authoredZoom, float currentZoom)
    {
        // Unity uses 100 sprite pixels per unit and SharpFAI uses 100 world units per Unity unit.
        float multiplier = item.ScaleMultiplier;
        if (item.LockScale)
            multiplier *= authoredZoom / currentZoom;
        return pixels * pose.TextureScale * pose.Scale * multiplier;
    }

    public void Dispose()
    {
        _animationTextures.Dispose();
        _staticTextures.Dispose();
        _batchBuffer.Dispose();
        GL.DeleteVertexArray(_batchVao);
        GL.DeleteProgram(_batchProgram);
        foreach (int texture in _textures.Values)
            GL.DeleteTexture(texture);
        if (_grabTexture != 0)
            GL.DeleteTexture(_grabTexture);
        foreach (int texture in _maskTargets)
            GL.DeleteTexture(texture);
        if (_maskFramebuffer != 0)
            GL.DeleteFramebuffer(_maskFramebuffer);
        GL.DeleteBuffer(_vbo);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
        GL.DeleteProgram(_blendProgram);
    }
}
