using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust.Geometry;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed class TrackGuideRenderer : IDisposable
{
    private readonly GlMeshUploader _uploader;
    private readonly Mesh _mesh;
    private readonly GlMeshHandle _handle;
    private readonly int _program;
    private readonly int _viewProjection;
    private readonly int _time;
    private readonly int _playing;

    // Owns the supplied mesh after successful upload.
    public TrackGuideRenderer(GlMeshUploader uploader, Mesh mesh)
    {
        _program = SpriteRenderer.LinkProgram("TrackGuide.vert", "TrackGuide.frag");
        _uploader = uploader;
        _mesh = mesh;
        try
        {
            uploader.Upload(mesh);
            if (!uploader.TryGetHandle(mesh, out _handle) || !_handle.IsValid)
            {
                throw new InvalidOperationException("Track guide mesh upload failed.");
            }

            _viewProjection = GL.GetUniformLocation(_program, "uViewProjection");
            _time = GL.GetUniformLocation(_program, "uTime");
            _playing = GL.GetUniformLocation(_program, "uPlaying");
        }
        catch
        {
            uploader.Release(mesh);
            GL.DeleteProgram(_program);
            throw;
        }
    }

    public void Draw(Matrix4 viewProjection, bool playing, double time)
    {
        GL.UseProgram(_program);
        GL.UniformMatrix4(_viewProjection, false, ref viewProjection);
        GL.Uniform1(_time, (float)time);
        GL.Uniform1(_playing, playing ? 1 : 0);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_handle.Vao);
        GL.DrawElements(PrimitiveType.Triangles, _mesh.IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        _uploader.Release(_mesh);
        _mesh.Dispose();
        GL.DeleteProgram(_program);
    }
}
