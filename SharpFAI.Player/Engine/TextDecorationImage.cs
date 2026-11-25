using Newtonsoft.Json.Linq;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

/// <summary>
/// Rasterises ADOFAI text decorations. The game renders them with a Unity UI <c>Text</c> on a
/// world-space canvas, so the metrics here follow that component: a font family per
/// <c>FontName</c>, 100 pixels per unit and centred alignment with horizontal overflow.
/// </summary>
internal static class TextDecorationImage
{
    /// <summary>Unity's UI Text default font size on the decoration prefab.</summary>
    private const float DefaultFontSize = 100f;

    /// <summary>Extra transparent margin so glyph edges are never clipped.</summary>
    private const int Padding = 4;

    /// <summary>Line spacing multipliers from the game's <c>RDString.GetFontDataForLanguage</c>.</summary>
    internal const float LatinLineSpacing = 0.75f;
    internal const float CjkLineSpacing = 1.0f;
    internal const float JapaneseLineSpacing = 1.1f;

    internal static string ReadText(JToken? value) => value switch
    {
        JValue { Type: JTokenType.String } text => text.Value<string>() ?? "",
        JObject localized => localized.Value<string>("en") ??
            localized.Properties().Select(property => property.Value)
                .OfType<JValue>().FirstOrDefault(token => token.Type == JTokenType.String)?.Value<string>() ?? "",
        _ => "",
    };

    /// <summary>Map an authored <c>FontName</c> to the family installed on the system.</summary>
    internal static string ResolveFamily(string? fontName) => fontName switch
    {
        "ComicSansMS" => "Comic Sans MS",
        "CourierNew" => "Courier New",
        "Georgia" => "Georgia",
        "Impact" => "Impact",
        "TimesNewRoman" => "Times New Roman",
        "Arial" => "Arial",
        _ => ApplicationFont.FamilyName,
    };

    internal static ImageDecoder.RgbaImage Rasterize(string value, string? fontName)
    {
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var richText = UnityRichText.Parse(normalized, DefaultFontSize);
        string text = richText.Text;
        if (text.Length == 0)
            return new ImageDecoder.RgbaImage(1, 1, new byte[4]);

        var options = new TextRasterizationOptions
        {
            FontFamily = ResolveFamily(fontName),
            FontSize = DefaultFontSize,
            FallbackFamilies = FallbackFamilies,
            Padding = Padding,
            Alignment = TextAlignment.Center,
            LineSpacing = LineSpacing(text),
        };
        if (richText.Rasterize(options) is not { } raster)
            return new ImageDecoder.RgbaImage(1, 1, new byte[4]);
        ImageDecoder.FlipVertically(raster.Pixels, raster.Width, raster.Height);
        return raster;
    }

    /// <summary>
    /// Line spacing the game applies for the language that owns the font. The game keys this off
    /// the system language; the text itself is the best signal available when rendering offline.
    /// </summary>
    internal static float LineSpacing(string text) =>
        ContainsJapanese(text) ? JapaneseLineSpacing
        : ContainsCjk(text) ? CjkLineSpacing
        : LatinLineSpacing;

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if (c is (>= '\u3040' and <= '\u30ff') or (>= '\u3400' and <= '\u4dbf')
                or (>= '\u4e00' and <= '\u9fff') or (>= '\uac00' and <= '\ud7af'))
                return true;
        }
        return false;
    }

    private static bool ContainsJapanese(string text)
    {
        foreach (char c in text)
        {
            if (c is (>= '\u3040' and <= '\u309f') or (>= '\u30a0' and <= '\u30ff'))
                return true;
        }
        return false;
    }

    internal static readonly IReadOnlyList<string> FallbackFamilies = [ApplicationFont.FamilyName];
}
