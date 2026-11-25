using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.ShaderGen;
using Thrust.Geometry;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed unsafe class PositionTrackGuides(GlMeshUploader uploader) : IDisposable
{
    private static readonly VertexDeclaration Layout = new(
        (VertexAttribute.Position, VertexFormat.Float2),
        (VertexAttribute.TexCoord0, VertexFormat.Float2),
        (VertexAttribute.Color0, VertexFormat.Float4));
    private Mesh? _mesh;
    private GlMeshHandle _handle = null!;
    private int _program, _texture, _projection, _time;
    private bool _original;

    internal bool HasGeometry => _mesh is not null;

    public void Update(IReadOnlyList<Floor> floors, FloorTrackState[] states, PlanetTrackFloor[] planets,
        IReadOnlyList<PlaybackEvent> events, float spacing)
    {
        Clear();
        _mesh = Build(floors, states, planets, events, spacing);
        if (_mesh is null) return;
        if (_program == 0) Initialize();
        uploader.Upload(_mesh);
        if (!uploader.TryGetHandle(_mesh, out _handle) || !_handle.IsValid)
            throw new InvalidOperationException("Position Track connector mesh upload failed.");
    }

    internal static Mesh? Build(IReadOnlyList<Floor> floors, FloorTrackState[] states, PlanetTrackFloor[] planets,
        IReadOnlyList<PlaybackEvent> events, float spacing)
    {
        Mesh? mesh = null;
        var earlierOffsets = new Dictionary<int, Vector2>();
        foreach (PlaybackEvent item in events)
        {
            if (item is not PositionTrack position || !position.IsActive ||
                position.Floor <= 0 || position.Floor >= floors.Count ||
                position.Enabled("justThisTile", position.JustThisTile) == true ||
                position.Enabled("positionOffset", position.PositionOffset) is not { } offset)
                continue;

            int previousIndex = position.Floor - 1;
            if (previousIndex < planets.Length && planets[previousIndex].HasHold) continue;
            Vector2 shift = new Vector2(offset.X ?? 0, offset.Y ?? 0) * spacing;
            float length = shift.Length;
            if (!float.IsFinite(length) || length < 5f) continue;

            Floor previous = floors[previousIndex];
            Vector2 start = new Vector2(previous.position.X, previous.position.Y) + states[previousIndex].Transform.Offset +
                PlanetTrackFloor.Direction(previous.angle) * (spacing * .5f) + earlierOffsets.GetValueOrDefault(position.Floor);
            Vector2 cross = new Vector2(-shift.Y, shift.X) * (5f / length);
            bool editorOnly = position.Enabled("editorOnly", position.EditorOnly) == true;
            Vector4 first = editorOnly ? new(.4f, 1f, .4f, 1f) : new(.75f, .5f, 1f, 1f);
            Vector4 last = editorOnly ? new(1f, 1f, .4f, 1f) : new(.4f, .4f, 1f, 1f);
            mesh ??= new Mesh(Layout, indexFormat: IndexFormat.UInt32) { Name = "PositionTrackConnectors" };
            uint baseIndex = (uint)mesh.VertexCount;
            // Positions are in hundredths of a Unity unit. LineTextureMode.Tile repeats per world unit.
            Vertex(mesh, start - cross, new Vector2(0, 0), first);
            Vertex(mesh, start + cross, new Vector2(0, 1), first);
            Vertex(mesh, start + shift - cross, new Vector2(length / 100f, 0), last);
            Vertex(mesh, start + shift + cross, new Vector2(length / 100f, 1), last);
            mesh.AppendIndex(baseIndex);
            mesh.AppendIndex(baseIndex + 1);
            mesh.AppendIndex(baseIndex + 2);
            mesh.AppendIndex(baseIndex + 2);
            mesh.AppendIndex(baseIndex + 1);
            mesh.AppendIndex(baseIndex + 3);
            earlierOffsets[position.Floor] = earlierOffsets.GetValueOrDefault(position.Floor) + shift;
        }
        mesh?.AddSubMeshForAll();
        return mesh;
    }

    private static void Vertex(Mesh mesh, Vector2 position, Vector2 uv, Vector4 color)
    {
        VertexWriter vertex = mesh.AppendVertex();
        vertex.SetVector2(VertexAttribute.Position, position);
        vertex.SetTexCoord(0, uv);
        vertex.SetColor(0, color);
    }

    private void Initialize()
    {
        string? shader = ShaderLibrary.TryLoad("Filters/ADOFAI/ScrollingSprite.glsl");
        if (shader is not null && EmbeddedAssets.Read("Resources.Filters.dotted-line.png") is { } bytes)
        {
            try
            {
                var image = ImageDecoder.Decode(bytes);
                ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
                _program = SpriteRenderer.LinkSources(UnityShaderAdapter.ImportExportedVertex(shader),
                    UnityShaderAdapter.ImportFullscreenFragment(shader), static program =>
                    {
                        GL.BindAttribLocation(program, 0, "in_POSITION0");
                        GL.BindAttribLocation(program, 1, "in_TEXCOORD0");
                        GL.BindAttribLocation(program, 2, "in_COLOR0");
                    });
                _texture = ImageDecoder.Upload(image, generateMipmaps: false, repeat: true);
                _original = true;
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SixLabors.ImageSharp.ImageFormatException)
            {
                if (_program != 0) GL.DeleteProgram(_program);
                _program = 0;
                Console.Error.WriteLine($"[Track] Position Track connector resources: {error.Message}");
            }
        }

        if (_program == 0)
            _program = SpriteRenderer.LinkSources(FallbackVertex, FallbackFragment);
        _projection = GL.GetUniformLocation(_program, _original ? "hlslcc_mtx4x4unity_MatrixVP[0]" : "uProjection");
        _time = GL.GetUniformLocation(_program, "_Time0");
        if (!_original) return;
        GL.UseProgram(_program);
        Matrix4 identity = Matrix4.Identity;
        GL.Uniform4(GL.GetUniformLocation(_program, "hlslcc_mtx4x4unity_ObjectToWorld[0]"), 4, (float*)&identity);
        GL.Uniform4(GL.GetUniformLocation(_program, "_Color"), Vector4.One);
        GL.Uniform4(GL.GetUniformLocation(_program, "_RendererColor"), Vector4.One);
        GL.Uniform2(GL.GetUniformLocation(_program, "_Flip"), Vector2.One);
        GL.Uniform2(GL.GetUniformLocation(_program, "_ScrollSpeed"), -.4f, 0f);
        GL.Uniform1(GL.GetUniformLocation(_program, "_MainTex"), 0);
        GL.UseProgram(0);
    }

    public void Draw(Matrix4 projection, double time)
    {
        if (_mesh is null) return;
        GL.UseProgram(_program);
        if (_original)
            GL.Uniform4(_projection, 4, (float*)&projection);
        else
            GL.UniformMatrix4(_projection, false, ref projection);
        GL.Uniform1(_time, (float)(time % 250.0));
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _texture);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_handle.Vao);
        GL.DrawElements(PrimitiveType.Triangles, _mesh.IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(0);
    }

    public void Clear()
    {
        if (_mesh is null) return;
        uploader.Release(_mesh);
        _mesh.Dispose();
        _mesh = null;
    }

    public void Dispose()
    {
        Clear();
        if (_texture != 0) GL.DeleteTexture(_texture);
        if (_program != 0) GL.DeleteProgram(_program);
    }

    // Basic guides are available when building without the original game resources.
    private const string FallbackVertex = """
        #version 330 core
        layout(location=0) in vec2 position;
        layout(location=1) in vec2 uv;
        layout(location=2) in vec4 tint;
        uniform mat4 uProjection;
        out vec2 coord;
        out vec4 color;
        void main() {
            gl_Position = uProjection * vec4(position, 0.0, 1.0);
            coord = uv;
            color = tint;
        }
        """;

    private const string FallbackFragment = """
        #version 330 core
        in vec2 coord;
        in vec4 color;
        uniform float _Time0;
        out vec4 result;
        void main() {
            float alpha = color.a * step(0.35, fract(4.0 * (coord.x - _Time0 * 0.4)));
            result = vec4(color.rgb * alpha, alpha);
        }
        """;
}
