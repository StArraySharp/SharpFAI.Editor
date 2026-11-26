using OpenTK.Graphics.OpenGL4;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed class ModSceneDepth : IDisposable
{
    private int _framebuffer, _texture, _width, _height;

    public unsafe int Render(int width, int height, Action draw)
    {
        using var state = new GlRenderStateScope();
        int read = GL.GetInteger(GetPName.ReadFramebufferBinding);
        var function = (DepthFunction)GL.GetInteger(GetPName.DepthFunc);
        bool mask = GL.GetBoolean(GetPName.DepthWritemask);
        try
        {
            if (_texture == 0 || width != _width || height != _height)
            {
                Dispose();
                _width = width;
                _height = height;
                _framebuffer = GL.GenFramebuffer();
                _texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, _texture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, width, height,
                    0, PixelFormat.DepthComponent, PixelType.UnsignedInt, 0);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D, _texture, 0);
                GL.DrawBuffer(DrawBufferMode.None);
                GL.ReadBuffer(ReadBufferMode.None);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                {
                    Dispose();
                    throw new InvalidOperationException("The Mod scene depth buffer is incomplete.");
                }
            }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffer);
            GL.Viewport(0, 0, width, height);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.CullFace);
            float clear = 1f;
            GL.ClearBuffer(ClearBuffer.Depth, 0, &clear);
            draw();
            return _texture;
        }
        finally
        {
            GL.DepthFunc(function);
            GL.DepthMask(mask);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
        }
    }

    public void Dispose()
    {
        if (_framebuffer != 0) GL.DeleteFramebuffer(_framebuffer);
        if (_texture != 0) GL.DeleteTexture(_texture);
        _framebuffer = _texture = 0;
    }
}
