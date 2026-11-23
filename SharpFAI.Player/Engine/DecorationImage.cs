using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal enum DecorationChannels { Gray = 1, GrayAlpha = 2, Rgb = 3, Rgba = 4 }

/// <summary>Decoded sprite pixels, with redundant color channels removed losslessly.</summary>
internal readonly record struct DecorationImage(int Width, int Height, byte[] Pixels,
    DecorationChannels Channels = DecorationChannels.Rgba)
{
    public int BytesPerPixel => (int)Channels;

    // Match the default GL unpack alignment, including for odd-width grayscale sprites.
    public int RowBytes => (Width * BytesPerPixel + 3) & ~3;
    public long TextureBytes => (long)Width * Height * BytesPerPixel;

    public static implicit operator DecorationImage(ImageDecoder.RgbaImage image) =>
        new(image.Width, image.Height, image.Pixels);

    public static DecorationImage Pack(ImageDecoder.RgbaImage image)
    {
        byte[] pixels = image.Pixels;
        if (pixels.Length < 1024 * 1024)
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
