using System.Collections.Concurrent;
using System.Globalization;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed class ChartTextFonts(IFontFallbackResolver systemResolver, IEnumerable<FontFamily> alternatives)
    : IFontFallbackResolver
{
    private static readonly ConcurrentDictionary<(FontFamily Family, FontStyle Style), bool> UsableFaces = new();
    private static readonly Lazy<FontFamily[]> AlternativeFamilies = new(() => ResolveFamilies(
        OperatingSystem.IsMacOS()
            ? ["Helvetica", "PingFang SC", "Hiragino Sans GB", "Heiti SC", "Songti SC", "Hiragino Sans",
                "Apple SD Gothic Neo", "Apple Color Emoji", "Arial Unicode MS"]
            : ["Arial", "DejaVu Sans", "Noto Sans CJK SC", "Noto Sans CJK JP", "Noto Sans CJK KR"]));

    internal static ChartTextFonts SystemFallback { get; } = new(SystemFonts.FallbackResolver, FallbackAlternatives());

    internal static FontFamily[] Resolve(TextRasterizationOptions options, IReadOnlyList<FontStyle> styles)
    {
        FontFamily[] preferred = ResolveFamilies(new[] { options.FontFamily }.Concat(options.FallbackFamilies ?? []));
        FontFamily[] usable = preferred.Where(family => styles.All(style => IsUsable(family, style))).ToArray();
        if (usable.Length != 0)
            return usable;

        foreach (FontFamily family in AlternativeFamilies.Value.Concat(SystemFonts.Families))
            if (styles.All(style => IsUsable(family, style)))
                return [family];
        throw new FontFamilyNotFoundException(options.FontFamily);
    }

    public bool TryResolve(CodePoint codePoint, FontFamily primary, FontStyle style, CultureInfo? culture,
        out FontFamily family)
    {
        try
        {
            if (systemResolver.TryResolve(codePoint, primary, style, culture, out family) &&
                IsUsable(family, style))
                return true;
        }
        catch (Exception error) when (IsFontError(error))
        {
            // The system resolver may inspect a font's tables before returning its family.
        }

        foreach (FontFamily alternative in alternatives)
        {
            if (alternative.Equals(primary) || !IsUsable(alternative, style)) continue;
            try
            {
                if (alternative.CreateFont(12f, style).TryGetGlyphs(codePoint, out _))
                {
                    family = alternative;
                    return true;
                }
            }
            catch (Exception error) when (IsFontError(error))
            {
            }
        }
        family = default;
        return false;
    }

    internal static bool IsFontError(Exception error) =>
        error is NotSupportedException or FontException or IOException or UnauthorizedAccessException;

    private static bool IsUsable(FontFamily family, FontStyle style) =>
        UsableFaces.GetOrAdd((family, style), static face =>
        {
            try
            {
                // Font creation reads the face description. Line metrics force the lazy outline table load.
                _ = face.Family.CreateFont(12f, face.Style).FontMetrics.HorizontalMetrics;
                return true;
            }
            catch (Exception error) when (IsFontError(error))
            {
                return false;
            }
        });

    private static FontFamily[] ResolveFamilies(IEnumerable<string> names) =>
        names.Select(name => name.Equals(ApplicationFont.FamilyName, StringComparison.OrdinalIgnoreCase)
                ? ApplicationFont.Family
                : SystemFonts.TryGet(name, out FontFamily family) ? family : (FontFamily?)null)
            .OfType<FontFamily>().Distinct().ToArray();

    private static IEnumerable<FontFamily> FallbackAlternatives()
    {
        // Look up additional Mac faces only when the normal resolver cannot supply a glyph.
        if (OperatingSystem.IsMacOS())
            foreach (FontFamily family in AlternativeFamilies.Value)
                yield return family;
    }
}
