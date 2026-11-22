using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;

namespace SharpFAI.Player.Tests;

public sealed class TestTextDecorationRaster
{
    [Test]
    public void RasterHasMostlyTransparentPixels()
    {
        var img = TextDecorationImage.Rasterize("x4", "Default");
        int opaque = 0, clear = 0, partial = 0;
        for (int i = 3; i < img.Pixels.Length; i += 4)
        {
            byte a = img.Pixels[i];
            if (a == 255) opaque++;
            else if (a == 0) clear++;
            else partial++;
        }
        int total = img.Width * img.Height;
        TestContext.Out.WriteLine($"size {img.Width}x{img.Height} opaque={opaque} clear={clear} partial={partial}");

        // A few glyphs on a mostly-transparent canvas: at most a quarter of the pixels should be
        // fully opaque, otherwise the raster is a filled rectangle rather than text.
        Assert.That(opaque, Is.LessThan(total / 4), "raster is filled, not glyphs");
        Assert.That(clear, Is.GreaterThan(total / 2), "raster has no transparent margin");
    }

    [Test]
    public void RasterIsNotUniformlyOpaqueForMultilineText()
    {
        var img = TextDecorationImage.Rasterize("Hello\nWorld", "Georgia");
        int clear = 0;
        for (int i = 3; i < img.Pixels.Length; i += 4)
            if (img.Pixels[i] == 0)
                clear++;
        TestContext.Out.WriteLine($"size {img.Width}x{img.Height} clear={clear}");
        Assert.That(clear, Is.GreaterThan(img.Width * img.Height / 2));
    }
}
