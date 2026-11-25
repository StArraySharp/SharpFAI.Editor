using ImGui4NET;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private sealed record RichLevelTextPreview(string Source, float FontSize, int Texture, int Width, int Height);

    private readonly Dictionary<string, RichLevelTextPreview> _richLevelTextPreviews = new(StringComparer.Ordinal);

    private JToken? DrawRichLevelTextField(EditorField field, JToken? value)
    {
        string current = value?.Value<string>() ?? "";
        float left = ImGui.GetCursorPosX();
        float width = ImGui.GetContentRegionAvail().x;
        string? updated = EditText("##value", field.Unit ?? "", current, live: false);
        DrawRichLevelTextPreview(field.Key, updated ?? current, left, width, ImGui.GetFrameHeight(), styledOnly: true);
        return updated is null ? null : new JValue(updated);
    }

    private bool DrawRichLevelTextPreview(string key, string source, float left, float width, float height, bool styledOnly)
    {
        float fontSize = ImGui.GetFontSize();
        if (!_richLevelTextPreviews.TryGetValue(key, out var preview) ||
            preview.Source != source || preview.FontSize != fontSize)
        {
            preview = CreateRichLevelTextPreview(source, fontSize, styledOnly);
            if (_richLevelTextPreviews.TryGetValue(key, out var old) && old.Texture != 0)
                GL.DeleteTexture(old.Texture);
            _richLevelTextPreviews[key] = preview;
        }

        if (preview.Texture == 0 || width <= 0 || height <= 0)
            return false;

        float scale = Math.Min(1f, Math.Min(width / preview.Width, height / preview.Height));
        ImGui.SetCursorPosX(left);
        ImGui.Image(new ImTextureRef { _TexID = (uint)preview.Texture },
            WidgetDraw.V2(preview.Width * scale, preview.Height * scale), WidgetDraw.V2(0, 0), WidgetDraw.V2(1, 1));
        return true;
    }

    private static RichLevelTextPreview CreateRichLevelTextPreview(string source, float fontSize, bool styledOnly)
    {
        if (string.IsNullOrWhiteSpace(source) || styledOnly && !source.Contains('<'))
            return new(source, fontSize, 0, 0, 0);

        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var text = UnityRichText.Parse(normalized, fontSize);
        if (text.Text.Length == 0 || styledOnly && text.Text == normalized)
            return new(source, fontSize, 0, 0, 0);

        var image = text.Rasterize(new TextRasterizationOptions
        {
            FontFamily = TextDecorationImage.ResolveFamily(null),
            FallbackFamilies = TextDecorationImage.FallbackFamilies,
            FontSize = fontSize,
            Padding = 2,
            MaxWidth = 2048,
            MaxHeight = 1024,
        });
        int oldBinding = GL.GetInteger(GetPName.TextureBinding2D);
        int texture = ImageDecoder.Upload(image, generateMipmaps: false, repeat: false);
        GL.BindTexture(TextureTarget.Texture2D, oldBinding);
        return new(source, fontSize, texture, image.Width, image.Height);
    }

    private void DisposeRichLevelTextPreviews()
    {
        foreach (var preview in _richLevelTextPreviews.Values)
            if (preview.Texture != 0)
                GL.DeleteTexture(preview.Texture);
        _richLevelTextPreviews.Clear();
    }
}
