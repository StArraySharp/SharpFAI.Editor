using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

/// <summary>Streams the ordered index and tile data while keeping the shape vertices on the GPU.</summary>
internal sealed unsafe class FloorStreamGpu : IDisposable
{
    private int _vao;
    private int _indexBuffer;
    private int _instanceBuffer;
    private int _instanceTexture;
    private int _shapeBuffer;
    private int _shapeTexture;
    private FloorShapeLibrary? _shapes;

    public static int GetTexelCapacity()
    {
        GL.GetInteger(GetPName.MaxVertexTextureImageUnits, out int vertexUnits);
        GL.GetInteger(GetPName.MaxTextureBufferSize, out int texels);
        return vertexUnits >= 3 ? texels : 0;
    }

    public void Upload(FloorStreamFrame frame)
    {
        if (_vao == 0)
        {
            _vao = GL.GenVertexArray();
            _indexBuffer = GL.GenBuffer();
            _instanceBuffer = GL.GenBuffer();
            _instanceTexture = GL.GenTexture();
            _shapeBuffer = GL.GenBuffer();
            _shapeTexture = GL.GenTexture();
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
                UploadTexture(_shapeBuffer, _shapeTexture, vertices.Length * sizeof(Vector4), (nint)data,
                    BufferUsageHint.StaticDraw);
            }
            _shapes = frame.Shapes;
        }

        fixed (FloorStreamInstance* instances = frame.Instances)
        {
            UploadTexture(_instanceBuffer, _instanceTexture, frame.Instances.Length * sizeof(FloorStreamInstance),
                (nint)instances, BufferUsageHint.StreamDraw);
        }
    }

    private static void UploadTexture(int buffer, int texture, int size, nint data, BufferUsageHint usage)
    {
        GL.BindBuffer(BufferTarget.TextureBuffer, buffer);
        GL.BufferData(BufferTarget.TextureBuffer, size, data, usage);
        GL.BindTexture(TextureTarget.TextureBuffer, texture);
        GL.TexBuffer(TextureBufferTarget.TextureBuffer, SizedInternalFormat.Rgba32f, buffer);
        GL.BindTexture(TextureTarget.TextureBuffer, 0);
        GL.BindBuffer(BufferTarget.TextureBuffer, 0);
    }

    public void Bind(int firstUnit)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit);
        GL.BindTexture(TextureTarget.TextureBuffer, _instanceTexture);
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit + 1);
        GL.BindTexture(TextureTarget.TextureBuffer, _shapeTexture);
        GL.BindVertexArray(_vao);
    }

    public static void Unbind(int firstUnit)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit);
        GL.BindTexture(TextureTarget.TextureBuffer, 0);
        GL.ActiveTexture(TextureUnit.Texture0 + firstUnit + 1);
        GL.BindTexture(TextureTarget.TextureBuffer, 0);
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
        _shapes = null;
    }
}
