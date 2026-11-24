using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

/// <summary>Rejects transparent sprite borders without changing rasterization or UV interpolation.</summary>
internal struct DecorationScissor
{
    private Box2i _viewport;
    private Box2i _previous;
    private bool _scissorEnabled;
    private bool _initialized;

    public Box2i? Clip(Matrix4 transform, Box2 alpha, Vector2 shaderViewport, bool smoothing)
    {
        if (alpha.Size == Vector2.Zero)
            return new Box2i();

        Vector2 x = transform.Row0.Xy, y = transform.Row1.Xy, origin = transform.Row3.Xy;
        // Extremely large coordinates can lose more than the scissor's pixel margin.
        if (!WithinRange(x) || !WithinRange(y) || !WithinRange(origin))
            return null;

        if (!_initialized)
        {
            _viewport = ReadBox(GetPName.Viewport);
            _scissorEnabled = GL.IsEnabled(EnableCap.ScissorTest);
            _previous = ReadBox(GetPName.ScissorBox);
            _initialized = true;
        }

        var viewport = _viewport;
        Vector2 halfViewport = new(viewport.Size.X * .5f, viewport.Size.Y * .5f);
        Vector2 projectedSize = new(MathF.Abs(x.X) + MathF.Abs(y.X), MathF.Abs(x.Y) + MathF.Abs(y.Y));
        projectedSize = projectedSize * halfViewport + new Vector2(4f);
        const int minSavedPixels = 8192;
        if (projectedSize.X * projectedSize.Y < minSavedPixels)
            return null;

        Vector2 footprint = Vector2.Zero;
        if (smoothing)
        {
            Vector2 px = x * (shaderViewport * .5f), py = y * (shaderViewport * .5f);
            float area = MathF.Abs(px.X * py.Y - px.Y * py.X);
            if (area > 1e-8f)
                footprint = new Vector2(MathF.Abs(py.X) + MathF.Abs(py.Y),
                    MathF.Abs(px.X) + MathF.Abs(px.Y)) / area;
        }

        // Mirror the vertex shader expansion where UVs clamp to the texture edge.
        Vector2 min = alpha.Min - new Vector2(.5f);
        Vector2 max = alpha.Max - new Vector2(.5f);
        if (alpha.Min.X == 0) min.X -= footprint.X * .5f;
        if (alpha.Min.Y == 0) min.Y -= footprint.Y * .5f;
        if (alpha.Max.X == 1) max.X += footprint.X * .5f;
        if (alpha.Max.Y == 1) max.Y += footprint.Y * .5f;

        var limits = _scissorEnabled ? _previous : _viewport;
        var cropped = ScreenBox(min, max);
        if (cropped.Size.X == 0 || cropped.Size.Y == 0)
            return cropped;
        if (cropped == limits)
            return null;

        var full = ScreenBox(-(Vector2.One + footprint) * .5f, (Vector2.One + footprint) * .5f);
        long saved = full.Size.X * (long)full.Size.Y - cropped.Size.X * (long)cropped.Size.Y;
        return saved >= minSavedPixels ? cropped : null;

        Box2i ScreenBox(Vector2 lower, Vector2 upper)
        {
            Vector2 x0 = x * lower.X, x1 = x * upper.X;
            Vector2 y0 = y * lower.Y, y1 = y * upper.Y;
            Vector2 low = (Vector2.ComponentMin(x0, x1) + Vector2.ComponentMin(y0, y1) + origin + Vector2.One) * halfViewport;
            Vector2 high = (Vector2.ComponentMax(x0, x1) + Vector2.ComponentMax(y0, y1) + origin + Vector2.One) * halfViewport;
            // Allow for floating-point and rasterizer rounding at the border.
            var start = new Vector2i(Pixel(low.X, viewport.Size.X, -2), Pixel(low.Y, viewport.Size.Y, -2)) + viewport.Min;
            var end = new Vector2i(Pixel(high.X, viewport.Size.X, 2), Pixel(high.Y, viewport.Size.Y, 2)) + viewport.Min;
            start = Vector2i.ComponentMax(start, limits.Min);
            end = Vector2i.ComponentMax(start, Vector2i.ComponentMin(end, limits.Max));
            return new Box2i(start, end);
        }
    }

    private static bool WithinRange(Vector2 value) => MathF.Abs(value.X) <= 4096f && MathF.Abs(value.Y) <= 4096f;

    private static int Pixel(float value, int extent, int padding) =>
        (int)Math.Clamp((padding < 0 ? MathF.Floor(value) : MathF.Ceiling(value)) + padding, 0, extent);

    private static unsafe Box2i ReadBox(GetPName name)
    {
        int* values = stackalloc int[4];
        GL.GetInteger(name, values);
        var start = new Vector2i(values[0], values[1]);
        return new Box2i(start, start + new Vector2i(values[2], values[3]));
    }

    public void Apply(Box2i region)
    {
        if (!_scissorEnabled)
            GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(region.Min.X, region.Min.Y, region.Size.X, region.Size.Y);
    }

    public void Restore()
    {
        GL.Scissor(_previous.Min.X, _previous.Min.Y, _previous.Size.X, _previous.Size.Y);
        if (!_scissorEnabled)
            GL.Disable(EnableCap.ScissorTest);
    }
}
