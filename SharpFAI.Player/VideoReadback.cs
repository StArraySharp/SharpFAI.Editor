using System.Buffers;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;

namespace SharpFAI.Player;

internal sealed class VideoReadback : IDisposable
{
    private readonly int[] _buffers;
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameBytes;
    private readonly PixelFormat _format;
    private readonly PixelType _type;
    private int _head;

    public int Pending { get; private set; }
    public bool Full => Pending == _buffers.Length;

    public VideoReadback(int width, int height, bool packedNv12)
    {
        _width = width;
        _height = height;
        _format = packedNv12 ? PixelFormat.Bgra : PixelFormat.Rgba;
        _type = packedNv12 ? PixelType.UnsignedInt8888Reversed : PixelType.UnsignedByte;
        _frameBytes = checked(width * height * 4);
        _buffers = new int[Math.Clamp((96 * 1024 * 1024) / _frameBytes, 1, packedNv12 ? 4 : 3)];
        GL.GenBuffers(_buffers.Length, _buffers);
        foreach (int buffer in _buffers)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, buffer);
            GL.BufferData(BufferTarget.PixelPackBuffer, _frameBytes, IntPtr.Zero, BufferUsageHint.StreamRead);
        }
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
    }

    public void Submit()
    {
        int tail = (_head + Pending) % _buffers.Length;
        GL.BindBuffer(BufferTarget.PixelPackBuffer, _buffers[tail]);
        GL.ReadPixels(0, 0, _width, _height, _format, _type, IntPtr.Zero);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        Pending++;
    }

    public byte[] Take()
    {
        byte[] pixels = ArrayPool<byte>.Shared.Rent(_frameBytes);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, _buffers[_head]);
        try
        {
            IntPtr mapped = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, _frameBytes,
                MapBufferAccessMask.MapReadBit);
            if (mapped == IntPtr.Zero)
                throw new InvalidOperationException("无法读取视频帧缓冲");

            bool valid;
            try
            {
                Marshal.Copy(mapped, pixels, 0, _frameBytes);
            }
            finally
            {
                valid = GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
            }
            if (!valid)
                throw new InvalidOperationException("视频帧缓冲已损坏");
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        }

        Pending--;
        _head = (_head + 1) % _buffers.Length;
        return pixels;
    }

    public void Dispose() => GL.DeleteBuffers(_buffers.Length, _buffers);
}
