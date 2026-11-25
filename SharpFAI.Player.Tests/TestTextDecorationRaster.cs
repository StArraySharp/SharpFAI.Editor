using System.Globalization;
using ImGui4NET;
using SharpFAI.Player.Engine;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using Thrust.Graphics;

namespace SharpFAI.Player.Tests;

public sealed class TestTextDecorationRaster
{
    [Test]
    public unsafe void InterfaceCjkFullWidthCharactersShareTheirAdvance()
    {
        ImGuiContext* previous = ImGui.GetCurrentContext();
        ImGuiContext* context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        try
        {
            ImGuiIO* io = ImGui.GetIO();
            io->IniFilename = null;
            io->DisplaySize = new ImVec2 { x = 640, y = 480 };
            io->BackendFlags |= (int)ImGuiBackendFlags.ImGuiBackendFlags_RendererHasTextures;
            ApplicationFont.LoadInterfaceFont(io, 17f);
            ImGui.NewFrame();
            float width = ImGui.CalcTextSize("关").x;
            Assert.That(width, Is.EqualTo(17f));
            foreach (char character in "闭恢复日本語カナ한글")
                Assert.That(ImGui.CalcTextSize(character.ToString()).x, Is.EqualTo(width), character.ToString());
            Assert.That(ImGui.CalcTextSize("i").x, Is.LessThan(ImGui.CalcTextSize("W").x));
            ImGui.EndFrame();
        }
        finally
        {
            ImGui.DestroyContext(context);
            ImGui.SetCurrentContext(previous);
        }
    }

    [TestCase(FontStyle.Regular)]
    [TestCase(FontStyle.Bold)]
    [TestCase(FontStyle.Italic)]
    public void BundledFontLoadsCjkGlyphsInRichTextStyles(FontStyle style)
    {
        var font = ApplicationFont.Family.CreateFont(17f, style);
        foreach (char character in "中文日本語한글")
            Assert.That(font.TryGetGlyphs(new CodePoint(character), out _), Is.True, character.ToString());
    }

    [Test]
    public void UnsupportedSystemFontFallsBackToACompatibleFace()
    {
        var family = ChartTextFonts.Resolve(new TextRasterizationOptions(), [FontStyle.Regular])[0];
        var resolver = new ChartTextFonts(new UnsupportedSystemFont(), [family]);

        Assert.That(resolver.TryResolve(new CodePoint('A'), default, FontStyle.Regular,
            CultureInfo.InvariantCulture, out var resolved), Is.True);
        Assert.That(resolved, Is.EqualTo(family));
    }

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

    private sealed class UnsupportedSystemFont : IFontFallbackResolver
    {
        public bool TryResolve(CodePoint codePoint, FontFamily primary, FontStyle style, CultureInfo? culture,
            out FontFamily family) => throw new NotSupportedException("Unsupported CFF font dictionary");
    }
}
