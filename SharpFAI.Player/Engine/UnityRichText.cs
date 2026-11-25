using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed partial record UnityRichText(string Text, IReadOnlyList<UnityRichText.Run> Runs)
{
    internal readonly record struct Style(FontStyle FontStyle, float Size, Color Color);
    internal readonly record struct Run(int Start, int End, Style Style);

    public static UnityRichText Parse(string source, float fontSize)
    {
        var plain = new StringBuilder(source.Length);
        var runs = new List<Run>();
        var stack = new Stack<(string Tag, Style Parent)>();
        var initial = new Style(FontStyle.Regular, fontSize, Color.White);
        var style = initial;
        int previous = 0;
        foreach (Match match in Tags().Matches(source))
        {
            string tag = match.Groups["tag"].Value.ToLowerInvariant();
            bool close = match.Groups["close"].Success;
            var next = style;
            if (tag is not ("material" or "quad"))
            {
                if (close)
                {
                    if (!stack.TryPeek(out var open) || open.Tag != tag) continue;
                    next = stack.Pop().Parent;
                }
                else
                {
                    string argument = match.Groups["arg"].Value.Trim().Trim('"', '\'');
                    if (!TryStyle(tag, argument, style, out next)) continue;
                    stack.Push((tag, style));
                }
            }
            Append(source.AsSpan(previous, match.Index - previous));
            previous = match.Index + match.Length;
            style = next;
        }
        Append(source.AsSpan(previous));
        return new(plain.ToString(), runs.Any(run => run.Style != initial) ? runs : []);

        void Append(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty) return;
            int start = plain.Length;
            plain.Append(span);
            if (runs.Count > 0 && runs[^1].Style == style)
                runs[^1] = runs[^1] with { End = plain.Length };
            else
                runs.Add(new(start, plain.Length, style));
        }
    }

    private static bool TryStyle(string tag, string argument, Style style, out Style next)
    {
        next = style;
        switch (tag)
        {
            case "b" when argument.Length == 0:
                next = style with { FontStyle = style.FontStyle | FontStyle.Bold };
                return true;
            case "i" when argument.Length == 0:
                next = style with { FontStyle = style.FontStyle | FontStyle.Italic };
                return true;
            case "size" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size > 0:
                next = style with { Size = Math.Min(size, 1024) };
                return true;
            case "color" when TryColor(argument, out var color):
                next = style with { Color = color };
                return true;
            default:
                return false;
        }
    }

    private static bool TryColor(string value, out Color color)
    {
        string hex = value.ToLowerInvariant() switch
        {
            "aqua" or "cyan" => "00ffff", "black" => "000000", "blue" => "0000ff",
            "brown" => "a52a2a", "darkblue" => "0000a0", "fuchsia" or "magenta" => "ff00ff",
            "green" => "008000", "grey" or "gray" => "808080", "lightblue" => "add8e6",
            "lime" => "00ff00", "maroon" => "800000", "navy" => "000080",
            "olive" => "808000", "orange" => "ffa500", "purple" => "800080",
            "red" => "ff0000", "silver" => "c0c0c0", "teal" => "008080",
            "white" => "ffffff", "yellow" => "ffff00",
            _ => value.StartsWith('#') ? value[1..] : "",
        };
        color = Color.White;
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint packed))
            return false;
        if (hex.Length == 6) packed = (packed << 8) | 255u;
        color = Color.FromPixel(new Rgba32((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed));
        return true;
    }

    public ImageDecoder.RgbaImage? Rasterize(TextRasterizationOptions options)
    {
        try
        {
            return RasterizeCore(options);
        }
        catch (Exception error) when (ChartTextFonts.IsFontError(error))
        {
            Console.Error.WriteLine($"[Fonts] Cannot render chart text: {error.Message}");
            return null;
        }
    }

    private ImageDecoder.RgbaImage RasterizeCore(TextRasterizationOptions options)
    {
        FontStyle[] requestedStyles = Runs.Select(run => run.Style.FontStyle).Prepend(FontStyle.Regular).Distinct().ToArray();
        FontFamily[] families = ChartTextFonts.Resolve(options, requestedStyles);
        FontFamily primary = families[0];
        Font font = primary.CreateFont(options.FontSize);
        int[] graphemes = Runs.Count > 0 ? StringInfo.ParseCombiningCharacters(Text) : [];
        var styles = new List<RichTextRun>(Runs.Count);
        foreach (var run in Runs)
        {
            int start = GraphemeIndex(run.Start);
            int end = GraphemeIndex(run.End);
            if (start == end) continue;
            styles.Add(new RichTextRun
            {
                Start = start,
                End = end,
                Font = primary.CreateFont(run.Style.Size, run.Style.FontStyle),
                Brush = Brushes.Solid(run.Style.Color),
            });
        }
        var draw = new RichTextOptions(font)
        {
            Dpi = 72,
            WrappingLength = -1,
            LineSpacing = options.LineSpacing,
            TextAlignment = options.Alignment switch
            {
                Thrust.Graphics.TextAlignment.Center => SixLabors.Fonts.TextAlignment.Center,
                Thrust.Graphics.TextAlignment.Right => SixLabors.Fonts.TextAlignment.End,
                _ => SixLabors.Fonts.TextAlignment.Start,
            },
            FallbackFontFamilies = families[1..],
            FontFallbackResolver = ChartTextFonts.SystemFallback,
            TextRuns = styles,
        };
        FontRectangle advance = TextMeasurer.MeasureAdvance(Text, draw);
        FontRectangle ink = TextMeasurer.MeasureBounds(Text, draw);
        float lineHeight = options.FontSize * options.LineSpacing;
        float fontLineHeight = font.FontMetrics.HorizontalMetrics.LineHeight * options.FontSize / font.FontMetrics.UnitsPerEm;
        float outset = MathF.Max(0, (fontLineHeight - lineHeight) * .5f);
        float left = MathF.Floor(MathF.Min(0, ink.Left));
        float top = MathF.Floor(MathF.Min(-outset, ink.Top));
        float right = MathF.Ceiling(MathF.Max(advance.Right, ink.Right));
        float bottom = MathF.Ceiling(MathF.Max(
            MathF.Max(advance.Height, (1 + Text.AsSpan().Count('\n')) * lineHeight) + outset, ink.Bottom));
        int width = (int)Math.Clamp(right - left + 2f * options.Padding, 1f, options.MaxWidth);
        int height = (int)Math.Clamp(bottom - top + 2f * options.Padding, 1f, options.MaxHeight);
        draw.Origin = new(options.Padding - left, options.Padding - top);
        byte[] pixels = new byte[checked(width * height * 4)];
        using var image = Image.WrapMemory<Rgba32>(pixels.AsMemory(), width, height);
        image.Mutate(context => context.Paint(canvas => canvas.DrawText(draw, Text.AsSpan(), Brushes.Solid(Color.White), null)));
        return new(width, height, pixels);

        int GraphemeIndex(int offset)
        {
            int index = Array.BinarySearch(graphemes, offset);
            return index < 0 ? ~index : index;
        }
    }

    [GeneratedRegex("""<(?<close>/)?(?<tag>b|i|color|size|material|quad)(?:\s*=\s*(?<arg>[^<>]*?))?(?:\s+[^<>]*)?\s*/?>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
}
