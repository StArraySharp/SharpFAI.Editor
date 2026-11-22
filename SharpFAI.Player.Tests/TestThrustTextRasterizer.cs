using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

public sealed class TestThrustTextRasterizer
{
    private static readonly TextRasterizationOptions Options = new()
    {
        FontFamily = "Arial", FontSize = 40, Padding = 4,
    };

    [TestCase("")]
    [TestCase("hello")]
    [TestCase("hello\nworld")]
    [TestCase("你好、こんにちは")]
    public void MeasurementMatchesTheOwnedRgbaBuffer(string text)
    {
        var size = TextRasterizer.Measure(text, Options);
        var raster = TextRasterizer.Rasterize(text, Options);
        Assert.Multiple(() =>
        {
            Assert.That((raster.Width, raster.Height), Is.EqualTo(size));
            Assert.That(raster.Pixels, Has.Length.EqualTo(size.Width * size.Height * 4));
            if (text.Length == 0)
                Assert.That(raster.Pixels, Is.All.Zero);
            else
                Assert.That(raster.Pixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 0), Is.True);
        });
    }

    [Test]
    public void ClippingAndLargePaddingDoNotOverflowDimensions()
    {
        var clipped = Options with { MaxWidth = 17, MaxHeight = 23 };
        var raster = TextRasterizer.Rasterize("long text\non two lines", clipped);
        Assert.Multiple(() =>
        {
            Assert.That((raster.Width, raster.Height), Is.EqualTo((17, 23)));
            Assert.That(raster.Pixels, Has.Length.EqualTo(17 * 23 * 4));
            Assert.That(TextRasterizer.Measure("x", clipped with { Padding = int.MaxValue }),
                Is.EqualTo((17, 23)));
        });
    }

    [Test]
    public void FontLineBoxDoesNotResizeWithOrdinaryAscendersAndDescenders()
    {
        var options = Options with { FontFamily = "Georgia" };
        Assert.That(TextRasterizer.Measure("hello", options).Height,
            Is.EqualTo(TextRasterizer.Measure("anybody here to save me?", options).Height));
    }

    [Test]
    public void ExplicitBlankLinesAndLineSpacingAffectTheBlockHeight()
    {
        int one = TextRasterizer.Measure("hello", Options).Height;
        int two = TextRasterizer.Measure("hello\n", Options).Height;
        int three = TextRasterizer.Measure("\nhello\n", Options).Height;
        int tight = TextRasterizer.Measure("hello\nworld", Options with { LineSpacing = .75f }).Height;
        int loose = TextRasterizer.Measure("hello\nworld", Options with { LineSpacing = 1.1f }).Height;
        Assert.Multiple(() =>
        {
            Assert.That(two, Is.GreaterThan(one));
            Assert.That(three, Is.GreaterThan(two));
            Assert.That(loose, Is.GreaterThan(tight));
        });
    }

    [TestCase(TextAlignment.Left)]
    [TestCase(TextAlignment.Center)]
    [TestCase(TextAlignment.Right)]
    public void ShorterLinesFollowTheRequestedAlignment(TextAlignment alignment)
    {
        // Compare the same glyph so side bearings cannot masquerade as an alignment error.
        var raster = TextRasterizer.Rasterize("WWWW\nW", Options with
        {
            Alignment = alignment, LineSpacing = 1.5f,
        });
        var first = InkBounds(raster, 0, raster.Height / 2);
        var second = InkBounds(raster, raster.Height / 2, raster.Height);
        float firstAnchor = Anchor(first, alignment);
        float secondAnchor = Anchor(second, alignment);
        Assert.That(secondAnchor, Is.EqualTo(firstAnchor).Within(2));
    }

    [Test]
    public void MissingPrimaryFamilyUsesTheRequestedFallback()
    {
        var fallback = Options with { FontFamily = "Thrust-Nonexistent-Font", FallbackFamilies = ["Arial"] };
        var expected = TextRasterizer.Rasterize("fallback", Options);
        var actual = TextRasterizer.Rasterize("fallback", fallback);
        Assert.Multiple(() =>
        {
            Assert.That((actual.Width, actual.Height), Is.EqualTo((expected.Width, expected.Height)));
            Assert.That(actual.Pixels, Is.EqualTo(expected.Pixels));
        });
    }

    [Test]
    public void ConcurrentRastersDoNotShareMutablePixels()
    {
        var images = new ImageDecoder.RgbaImage[8];
        Parallel.For(0, images.Length, i => images[i] = TextRasterizer.Rasterize("independent", Options));
        byte[] expected = (byte[])images[0].Pixels.Clone();
        Array.Clear(images[0].Pixels);
        foreach (var image in images.Skip(1))
            Assert.That(image.Pixels, Is.EqualTo(expected));
    }

    private static IEnumerable<TextRasterizationOptions> InvalidOptions()
    {
        yield return Options with { FontSize = 0 };
        yield return Options with { FontSize = float.NaN };
        yield return Options with { FontSize = float.PositiveInfinity };
        yield return Options with { LineSpacing = 0 };
        yield return Options with { LineSpacing = float.NaN };
        yield return Options with { Padding = -1 };
        yield return Options with { MaxWidth = 0 };
        yield return Options with { MaxHeight = -1 };
        yield return Options with { Alignment = (TextAlignment)99 };
    }

    [TestCaseSource(nameof(InvalidOptions))]
    public void InvalidLayoutOptionsFailBeforeAllocatingPixels(TextRasterizationOptions options) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TextRasterizer.Rasterize("text", options));

    private static (int Left, int Right) InkBounds(ImageDecoder.RgbaImage image, int top, int bottom)
    {
        int left = image.Width, right = -1;
        for (int y = top; y < bottom; y++)
        for (int x = 0; x < image.Width; x++)
        {
            if (image.Pixels[(y * image.Width + x) * 4 + 3] <= 16)
                continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }
        Assert.That(right, Is.GreaterThanOrEqualTo(left), "Expected visible glyphs in this line.");
        return (left, right);
    }

    private static float Anchor((int Left, int Right) bounds, TextAlignment alignment) => alignment switch
    {
        TextAlignment.Left => bounds.Left,
        TextAlignment.Right => bounds.Right,
        _ => (bounds.Left + bounds.Right) * .5f,
    };
}
