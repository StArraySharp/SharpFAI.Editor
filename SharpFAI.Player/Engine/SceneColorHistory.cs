using OpenTK.Graphics.OpenGL4;

namespace SharpFAI.Player.Engine;

internal sealed class SceneColorHistory : IDisposable
{
    private int _framebuffer;
    private int _texture;
    private int _width;
    private int _height;
    private bool _valid;
    private double _time;

    public void Restore(double time)
    {
        if (!_valid || time < _time)
        {
            Reset();
            return;
        }

        Span<int> viewport = stackalloc int[4];
        unsafe { fixed (int* view = viewport) GL.GetInteger(GetPName.Viewport, view); }
        if (viewport[2] != _width || viewport[3] != _height)
        {
            Reset();
            return;
        }
        Copy(viewport, restore: true);
    }

    public void Capture(double time)
    {
        Span<int> viewport = stackalloc int[4];
        unsafe { fixed (int* view = viewport) GL.GetInteger(GetPName.Viewport, view); }
        int width = viewport[2];
        int height = viewport[3];
        if (width <= 0 || height <= 0)
            return;

        if (_texture == 0 || width != _width || height != _height)
        {
            if (_texture == 0)
            {
                _texture = GL.GenTexture();
                _framebuffer = GL.GenFramebuffer();
            }
            int texture = GL.GetInteger(GetPName.TextureBinding2D);
            int draw = GL.GetInteger(GetPName.DrawFramebufferBinding);
            GL.BindTexture(TextureTarget.Texture2D, _texture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _texture, 0);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            _width = width;
            _height = height;
        }

        Copy(viewport, restore: false);
        _valid = true;
        _time = time;
    }

    private void Copy(ReadOnlySpan<int> viewport, bool restore)
    {
        int read = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int draw = GL.GetInteger(GetPName.DrawFramebufferBinding);
        bool scissor = GL.IsEnabled(EnableCap.ScissorTest);
        if (scissor)
            GL.Disable(EnableCap.ScissorTest);

        int x = viewport[0];
        int y = viewport[1];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, restore ? _framebuffer : draw);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, restore ? draw : _framebuffer);
        if (restore)
            GL.BlitFramebuffer(0, 0, _width, _height, x, y, x + _width, y + _height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        else
            GL.BlitFramebuffer(x, y, x + _width, y + _height, 0, 0, _width, _height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
        if (scissor)
            GL.Enable(EnableCap.ScissorTest);
    }

    public void Reset() => _valid = false;

    public void Dispose()
    {
        if (_texture != 0)
            GL.DeleteTexture(_texture);
        if (_framebuffer != 0)
            GL.DeleteFramebuffer(_framebuffer);
    }
}
