using OpenTK.Graphics.OpenGL4;
using SharpFAI.Player.Engine;

namespace SharpFAI.Player;

internal sealed class VideoColorConverter : IDisposable
{
    private readonly int[] _textures = new int[2];
    private readonly int[] _framebuffers = new int[2];
    private readonly int _vao;
    private readonly int _program;
    private int _next;

    public int Width { get; }
    public int Height { get; }

    public VideoColorConverter(int width, int height)
    {
        Width = width / 4;
        Height = height + height / 2;
        _program = SpriteRenderer.LinkProgram("Background.vert", "VideoNv12.frag");
        _vao = GL.GenVertexArray();
        GL.GenFramebuffers(_framebuffers.Length, _framebuffers);
        GL.GenTextures(_textures.Length, _textures);
        for (int i = 0; i < _textures.Length; i++)
        {
            GL.BindTexture(TextureTarget.Texture2D, _textures[i]);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Width, Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[i]);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _textures[i], 0);
            FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                Dispose();
                throw new InvalidOperationException($"视频色彩转换缓冲不完整：{status}");
            }
        }
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Pack(int source)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffers[_next]);
        _next = (_next + 1) % _framebuffers.Length;
        GL.Viewport(0, 0, Width, Height);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(_program);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, source);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.UseProgram(0);
    }

    public void Dispose()
    {
        GL.DeleteFramebuffers(_framebuffers.Length, _framebuffers);
        GL.DeleteTextures(_textures.Length, _textures);
        GL.DeleteVertexArray(_vao);
        GL.DeleteProgram(_program);
    }
}
