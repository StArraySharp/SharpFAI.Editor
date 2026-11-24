using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using OpenTK.Mathematics;
using Thrust.Graphics;
using DecoderOptions = SixLabors.ImageSharp.Formats.DecoderOptions;

namespace SharpFAI.Player.Engine;

internal enum DecorationChannels { Gray = 1, GrayAlpha = 2, Rgb = 3, Rgba = 4 }

/// <summary>Sprite pixels for GPU upload and the original dimensions used for placement.</summary>
internal readonly record struct DecorationImage(int Width, int Height, byte[] Pixels,
    DecorationChannels Channels = DecorationChannels.Rgba)
{
    public int SourceWidth { get; private init; }
    public int SourceHeight { get; private init; }
    // Null denotes an unbounded (or unexamined) sprite. The bounds are in source UVs.
    public Box2? AlphaBounds { get; private init; }
    public int LayoutWidth => SourceWidth == 0 ? Width : SourceWidth;
    public int LayoutHeight => SourceHeight == 0 ? Height : SourceHeight;

    public int BytesPerPixel => (int)Channels;

    // Match the default GL unpack alignment, including for odd-width grayscale sprites.
    public int RowBytes => (Width * BytesPerPixel + 3) & ~3;
    public long TextureBytes => (long)Width * Height * BytesPerPixel;
    // Drivers may back RGB8 with four bytes per texel.
    public long StorageBytes => Channels == DecorationChannels.Rgb ? (long)Width * Height * 4 : TextureBytes;

    public static implicit operator DecorationImage(ImageDecoder.RgbaImage image) =>
        new(image.Width, image.Height, image.Pixels);

    private static readonly DecoderOptions ResizeDecoder = new()
    {
        Configuration = Configuration.Default.Clone(),
    };

    static DecorationImage() => ResizeDecoder.Configuration.MaxDegreeOfParallelism = 1;

    public static DecorationImage ReadFile(string path, int texturePercent)
    {
        if (texturePercent == 100)
        {
            var original = ImageDecoder.DecodeFile(path);
            ImageDecoder.FlipVertically(original.Pixels, original.Width, original.Height);
            return Pack(original);
        }

        using var image = Image.Load<Rgba32>(ResizeDecoder, path);
        int sourceWidth = image.Width, sourceHeight = image.Height;
        int width = Math.Max(1, (int)Math.Round(sourceWidth * (texturePercent / 100d)));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * (texturePercent / 100d)));
        if (width != sourceWidth || height != sourceHeight)
        {
            image.Mutate(operation => operation.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Box,
                PremultiplyAlpha = true,
            }));
        }
        byte[] pixels = new byte[width * height * 4];
        image.CopyPixelDataTo(pixels);
        ImageDecoder.FlipVertically(pixels, width, height);
        return Pack(new ImageDecoder.RgbaImage(width, height, pixels)) with
        {
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
        };
    }

    public static DecorationImage Pack(ImageDecoder.RgbaImage image)
    {
        return PackChannels(image) with { AlphaBounds = FindAlphaBounds(image) };
    }

    private static Box2? FindAlphaBounds(ImageDecoder.RgbaImage image)
    {
        int width = image.Width, height = image.Height;
        var pixels = image.Pixels;
        int left = width, right = -1, bottom = height, top = -1;
        for (int y = 0; y < height; y++)
        {
            int start = (y * width) * 4 + 3;
            int first = 0;
            while (first < width && pixels[start + first * 4] == 0)
                first++;
            if (first == width)
                continue;
            int last = width - 1;
            while (last > first && pixels[start + last * 4] == 0)
                last--;
            left = Math.Min(left, first);
            right = Math.Max(right, last);
            bottom = Math.Min(bottom, y);
            top = y;
            if (left == 0 && right == width - 1 && bottom == 0 && top == height - 1)
                return null;
        }

        if (right < 0)
            return new Box2(Vector2.Zero, Vector2.Zero);

        // Include all neighboring bilinear samples, even in point-sampled sprites
        // so the same bounds can be used when a chart toggles image smoothing.
        var min = new Vector2(Math.Max(0, left - 1) / (float)width, Math.Max(0, bottom - 1) / (float)height);
        var max = new Vector2(Math.Min(width, right + 2) / (float)width, Math.Min(height, top + 2) / (float)height);
        return min == Vector2.Zero && max == Vector2.One ? null : new Box2(min, max);
    }

    private static DecorationImage PackChannels(ImageDecoder.RgbaImage image)
    {
        byte[] pixels = image.Pixels;
        if (pixels.Length < 16 * 1024)
            return image;

        bool opaque = true, grayscale = true;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            grayscale &= pixels[index] == pixels[index + 1] && pixels[index] == pixels[index + 2];
            opaque &= pixels[index + 3] == 255;
            if (!grayscale && !opaque)
                return image;
        }

        var channels = DecorationChannels.Rgb;
        if (grayscale)
            channels = opaque ? DecorationChannels.Gray : DecorationChannels.GrayAlpha;
        int rowBytes = (image.Width * (int)channels + 3) & ~3;
        byte[] packed = new byte[rowBytes * image.Height];
        int source = 0;
        for (int row = 0; row < image.Height; row++)
        {
            int destination = row * rowBytes;
            for (int column = 0; column < image.Width; column++, source += 4)
            {
                packed[destination++] = pixels[source];
                if (!grayscale)
                {
                    packed[destination++] = pixels[source + 1];
                    packed[destination++] = pixels[source + 2];
                }
                else if (!opaque)
                    packed[destination++] = pixels[source + 3];
            }
        }
        return new(image.Width, image.Height, packed, channels);
    }
}
