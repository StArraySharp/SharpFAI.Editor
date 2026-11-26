using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace SharpFAI.Player;

/// <summary>
/// Reads the framebuffer back to a PNG. Used by the command-line capture mode so a rendering problem
/// can be inspected as an image instead of through a screenshot of the window.
/// </summary>
internal static unsafe class FrameCapture
{
    public static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public static byte[] EncodeJpeg(byte[] pixels, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoder { Quality = 85 });
        return output.ToArray();
    }

    /// <summary>
    /// Grab the back buffer and write it as a PNG. OpenGL returns rows bottom-up, so they are
    /// flipped on the way out.
    /// </summary>
    /// <param name="path">Destination file; parent directories are created.</param>
    /// <param name="width">Framebuffer width in pixels.</param>
    /// <param name="height">Framebuffer height in pixels.</param>
    public static void Save(string path, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        byte[] pixels = ReadPixels(width, height);
        SaveRgba(path, pixels, width, height, alreadyFlipped: true);
    }

    /// <summary>
    /// 把后缓冲读成自上而下的 RGBA 数据（行序已翻转为图片顺序）。
    /// Read the back buffer into top-down RGBA rows, already flipped into image order.
    /// </summary>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    public static byte[] ReadPixels(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        int stride = width * 4;
        byte[] raw = new byte[stride * height];

        // 按 4 字节对齐读取，再整体翻转行序
        // Read with 4-byte alignment, then flip the row order in one pass
        GL.PixelStore(PixelStoreParameter.PackAlignment, 4);

        fixed (byte* data = raw)
        {
            GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, (nint)data);
        }

        byte[] flipped = new byte[raw.Length];

        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(raw, (height - 1 - y) * stride, flipped, y * stride, stride);
        }

        return flipped;
    }

    /// <summary>
    /// 把自上而下的 RGBA 数据写成 PNG。目录会自动创建。
    /// Write top-down RGBA pixels as a PNG, creating the directory as needed.
    /// </summary>
    /// <param name="path">目标文件 / Destination file.</param>
    /// <param name="pixels">自上而下的 RGBA 数据 / Top-down RGBA pixels.</param>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="alreadyFlipped">
    /// 数据是否已经是自上而下。为 false 时按 OpenGL 的自下而上顺序翻转。
    /// Whether the rows are already top-down; when false they are treated as OpenGL bottom-up.
    /// </param>
    public static void SaveRgba(string path, byte[] pixels, int width, int height, bool alreadyFlipped = true)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
        {
            return;
        }

        using Image<Rgba32> image = new(width, height);

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                int sourceRow = alreadyFlipped ? y : height - 1 - y;
                ReadOnlySpan<byte> bytes = pixels.AsSpan(sourceRow * width * 4, width * 4);
                MemoryMarshal.Cast<byte, Rgba32>(bytes).CopyTo(accessor.GetRowSpan(y));
            }
        });

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        image.SaveAsPng(full);
    }
}
