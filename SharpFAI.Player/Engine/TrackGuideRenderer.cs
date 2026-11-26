using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed unsafe class TrackGuideRenderer : IDisposable
{
    private readonly GlMeshUploader _uploader;
    private readonly TrackGuideMesh _geometry;
    private readonly GlMeshHandle _handle;
    private readonly int _program;
    private readonly int _viewProjection;
    private readonly int _time;
    private readonly int _playing;
    private readonly int _tilesOnly;
    private Vector2[]? _opacityPixels;
    private int _opacityTexture;
    private RuntimeTrackTimeline? _opacityRuntime;
    private double _opacityTime;
    private int _opacityRevision;
    private bool _opacityHidePassed;

    public bool HasHolds => _geometry.HoldFloors.Length != 0;

    // Owns the supplied geometry after successful upload.
    public TrackGuideRenderer(GlMeshUploader uploader, TrackGuideMesh geometry)
    {
        _program = SpriteRenderer.LinkProgram("TrackGuide.vert", "TrackGuide.frag");
        _uploader = uploader;
        _geometry = geometry;
        try
        {
            uploader.Upload(geometry.Mesh);
            if (!uploader.TryGetHandle(geometry.Mesh, out _handle) || !_handle.IsValid)
            {
                throw new InvalidOperationException("Track guide mesh upload failed.");
            }

            _viewProjection = GL.GetUniformLocation(_program, "uViewProjection");
            _time = GL.GetUniformLocation(_program, "uTime");
            _playing = GL.GetUniformLocation(_program, "uPlaying");
            _tilesOnly = GL.GetUniformLocation(_program, "uTilesOnly");
        }
        catch
        {
            uploader.Release(geometry.Mesh);
            GL.DeleteProgram(_program);
            throw;
        }
    }

    public void Draw(Matrix4 viewProjection, RuntimeTrackTimeline? runtime, double time, bool tilesOnly, bool hidePassed)
    {
        bool playing = runtime is not null && !tilesOnly;
        bool sampleOpacity = playing && HasHolds;
        if (sampleOpacity)
            BindRuntimeOpacity(runtime!, time, hidePassed);
        GL.UseProgram(_program);
        GL.UniformMatrix4(_viewProjection, false, ref viewProjection);
        GL.Uniform1(_time, (float)time);
        GL.Uniform1(_playing, playing ? 1 : 0);
        GL.Uniform1(_tilesOnly, tilesOnly ? 1 : 0);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_handle.Vao);
        GL.DrawElements(PrimitiveType.Triangles, _geometry.Mesh.IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(0);
        if (sampleOpacity)
            GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void BindRuntimeOpacity(RuntimeTrackTimeline runtime, double time, bool hidePassed)
    {
        int[] floors = _geometry.HoldFloors;
        int width = Math.Min(floors.Length, 256), height = (floors.Length + width - 1) / width;
        GL.ActiveTexture(TextureUnit.Texture0);
        if (_opacityTexture == 0)
        {
            _opacityTexture = GL.GenTexture();
            _opacityPixels = new Vector2[width * height];
            GL.BindTexture(TextureTarget.Texture2D, _opacityTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rg32f, width, height,
                0, PixelFormat.Rg, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
        }
        else
            GL.BindTexture(TextureTarget.Texture2D, _opacityTexture);

        int revision = runtime.Triggers.Revision;
        if (ReferenceEquals(_opacityRuntime, runtime) && _opacityTime == time &&
            _opacityRevision == revision && _opacityHidePassed == hidePassed)
            return;

        for (int i = 0; i < floors.Length; i++)
        {
            int floor = floors[i];
            // The hold starts at the previous tile and ends at the landing tile.
            _opacityPixels![i] = new Vector2(Opacity(Math.Max(0, floor - 1)), Opacity(floor + 1));
        }
        fixed (Vector2* pixels = _opacityPixels)
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Rg, PixelType.Float, (nint)pixels);
        _opacityRuntime = runtime;
        _opacityTime = time;
        _opacityRevision = revision;
        _opacityHidePassed = hidePassed;

        float Opacity(int floor) => runtime.TransformAt(floor, time, hidePassed).Opacity;
    }

    public void Dispose()
    {
        if (_opacityTexture != 0)
            GL.DeleteTexture(_opacityTexture);
        _uploader.Release(_geometry.Mesh);
        _geometry.Dispose();
        GL.DeleteProgram(_program);
    }
}
