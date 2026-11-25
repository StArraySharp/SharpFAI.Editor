using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

/// <summary>Streams the ordered index and tile data while keeping the shape vertices on the GPU.</summary>
internal sealed unsafe class FloorStreamGpu(bool useTexture2D) : IDisposable
{
    internal const int TextureRowWidth = 1024;

    private int _vao;
    private int _indexBuffer;
    private int _instanceBuffer;
    private int _instanceTexture;
    private int _shapeBuffer;
    private int _shapeTexture;
    private int _shapeRows;
    private int _instanceRows;
    private FloorShapeLibrary? _shapes;

    public static bool DefaultUseTexture2D => OperatingSystem.IsMacOS();
    public bool UsesTexture2D => useTexture2D;

    public static int GetTexelCapacity()
    {
        GL.GetInteger(GetPName.MaxVertexTextureImageUnits, out int vertexUnits);
        if (vertexUnits < 3)
            return 0;
        if (DefaultUseTexture2D)
        {
            GL.GetInteger(GetPName.MaxTextureSize, out int size);
            return size >= TextureRowWidth ? TextureRowWidth * size : 0;
        }
        GL.GetInteger(GetPName.MaxTextureBufferSize, out int texels);
        return texels;
    }

    public void Upload(FloorStreamFrame frame)
    {
        if (_vao == 0)
        {
            _vao = GL.GenVertexArray();
            _indexBuffer = GL.GenBuffer();
            _instanceTexture = GL.GenTexture();
            _shapeTexture = GL.GenTexture();
            if (!useTexture2D)
            {
                _instanceBuffer = GL.GenBuffer();
                _shapeBuffer = GL.GenBuffer();
            }
        }

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
        fixed (int* indices = frame.Indices)
        {
            GL.BufferData(BufferTarget.ElementArrayBuffer, frame.IndexCount * sizeof(int), (nint)indices,
                BufferUsageHint.StreamDraw);
        }
        GL.BindVertexArray(0);

        if (!ReferenceEquals(_shapes, frame.Shapes))
        {
            ReadOnlySpan<Vector4> vertices = frame.Shapes.Vertices;
            fixed (Vector4* data = vertices)
            {
                UploadTexture(_shapeBuffer, _shapeTexture, vertices.Length, (Vector4*)data,
                    BufferUsageHint.StaticDraw, ref _shapeRows);
            }
            _shapes = frame.Shapes;
        }

        fixed (FloorStreamInstance* instances = frame.Instances)
        {
            UploadTexture(_instanceBuffer, _instanceTexture, frame.InstanceTexels,
                (Vector4*)instances, BufferUsageHint.StreamDraw, ref _instanceRows);
        }
    }

    private void UploadTexture(int buffer, int texture, int texels, Vector4* data, BufferUsageHint usage,
        ref int allocatedRows)
    {
        if (useTexture2D)
        {
            UploadTexture2D(texture, texels, data, ref allocatedRows);
            return;
        }
        GL.BindBuffer(BufferTarget.TextureBuffer, buffer);
        GL.BufferData(BufferTarget.TextureBuffer, texels * sizeof(Vector4), (nint)data, usage);
        GL.BindTexture(TextureTarget.TextureBuffer, texture);
        GL.TexBuffer(TextureBufferTarget.TextureBuffer, SizedInternalFormat.Rgba32f, buffer);
        GL.BindTexture(TextureTarget.TextureBuffer, 0);
        GL.BindBuffer(BufferTarget.TextureBuffer, 0);
    }

    private static void UploadTexture2D(int texture, int texels, Vector4* data, ref int allocatedRows)
    {
        const TextureTarget target = TextureTarget.Texture2D;
        GL.BindTexture(target, texture);
        int fullRows = texels / TextureRowWidth;
        int tail = texels % TextureRowWidth;
        int rows = fullRows + (tail == 0 ? 0 : 1);
        if (allocatedRows == 0)
        {
            GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }
        if (rows > allocatedRows)
        {
            GL.TexImage2D(target, 0, PixelInternalFormat.Rgba32f, TextureRowWidth, rows, 0,
                PixelFormat.Rgba, PixelType.Float, 0);
            allocatedRows = rows;
        }
        if (fullRows > 0)
            GL.TexSubImage2D(target, 0, 0, 0, TextureRowWidth, fullRows, PixelFormat.Rgba, PixelType.Float, (nint)data);
        if (tail > 0)
            GL.TexSubImage2D(target, 0, 0, fullRows, tail, 1, PixelFormat.Rgba, PixelType.Float,
                (nint)(data + fullRows * TextureRowWidth));
        GL.BindTexture(target, 0);
    }

    public void Bind(int firstUnit, int fallbackTexture = 0)
    {
        TextureTarget target = useTexture2D ? TextureTarget.Texture2D : TextureTarget.TextureBuffer;
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit);
        GL.BindTexture(target, fallbackTexture == 0 ? _instanceTexture : fallbackTexture);
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit + 1);
        GL.BindTexture(target, fallbackTexture == 0 ? _shapeTexture : fallbackTexture);
    }

    public void BindVertexArray() => GL.BindVertexArray(_vao);

    public void Unbind(int firstUnit)
    {
        TextureTarget target = useTexture2D ? TextureTarget.Texture2D : TextureTarget.TextureBuffer;
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit);
        GL.BindTexture(target, 0);
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit + 1);
        GL.BindTexture(target, 0);
    }

    public void Dispose()
    {
        if (_vao == 0)
        {
            return;
        }
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_indexBuffer);
        GL.DeleteTexture(_instanceTexture);
        GL.DeleteBuffer(_instanceBuffer);
        GL.DeleteTexture(_shapeTexture);
        GL.DeleteBuffer(_shapeBuffer);
        _vao = 0;
        _shapeRows = _instanceRows = 0;
        _shapes = null;
    }
}
