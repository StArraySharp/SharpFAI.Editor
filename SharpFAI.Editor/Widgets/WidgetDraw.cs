using ImGui4NET;
using OpenTK.Mathematics;

namespace SharpFAI.Editor.Widgets;

/// <summary>
/// Shared helpers for custom widgets.
/// </summary>
public static unsafe class WidgetDraw
{
    /// <summary>Convert a float color to ImU32.</summary>
    public static uint U32(float r, float g, float b, float a = 1f)
    {
        ImVec4 color = new() { x = r, y = g, z = b, w = a };
        return ImGui.ColorConvertFloat4ToU32(in color);
    }

    public static uint U32(in Vector4 color) => U32(color.X, color.Y, color.Z, color.W);

    public static uint U32(byte r, byte g, byte b, byte a = 255)
        => U32(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Sample the current theme color.</summary>
    public static uint ThemeColor(ImGuiCol color, float alpha = 1f) => ImGui.GetColorU32((int)color, alpha);

    public static ImVec2 V2(float x, float y) => new() { x = x, y = y };

    public static ImVec2 V2(in Vector2 v) => new() { x = v.X, y = v.Y };

    public static Vector2 ToVector2(in ImVec2 v) => new(v.x, v.y);

    public static ImVec4 V4(float r, float g, float b, float a) => new() { x = r, y = g, z = b, w = a };

    /// <summary>Build a normalized rect from two corners.</summary>
    public static (ImVec2 Min, ImVec2 Max) Rect(in ImVec2 a, in ImVec2 b) => (
        V2(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y)),
        V2(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y)));

    /// <summary>Draw a rounded panel.</summary>
    public static void Panel(ImDrawList* draw, in ImVec2 min, in ImVec2 max, uint color, float rounding = 6f)
        => ImDrawList.AddRectFilled(draw, in min, in max, color, rounding);

    /// <summary>Draw a thin outline.</summary>
    public static void Outline(ImDrawList* draw, in ImVec2 min, in ImVec2 max, uint color, float rounding = 6f, float thickness = 1f)
        => ImDrawList.AddRect(draw, in min, in max, color, rounding, thickness);

    /// <summary>Draw one line of text vertically centered in a band.</summary>
    public static void TextCentered(ImDrawList* draw, in ImVec2 min, in ImVec2 max, uint color, string text)
    {
        ImVec2 size = ImGui.CalcTextSize(text, null, 0, -1f);
        ImVec2 pos = V2(
            min.x + (max.x - min.x - size.x) * 0.5f,
            min.y + (max.y - min.y - size.y) * 0.5f);

        ImDrawList.AddText(draw, in pos, color, text);
    }

    /// <summary>Draw wrapped text.</summary>
    public static void TextWrapped(ImDrawList* draw, in ImVec2 pos, float wrapWidth, uint color, string text)
        => ImDrawList.AddText(draw, ImGui.GetFont(), ImGui.GetFontSize(), in pos, color, text, null, wrapWidth);

    /// <summary>Smoothstep easing for animations.</summary>
    public static float SmoothStep(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Blend toward another packed color.</summary>
    public static uint Blend(uint from, uint to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        static uint Channel(uint c, int shift) => (c >> shift) & 0xFF;

        uint a = (uint)(Channel(from, 24) + (Channel(to, 24) - Channel(from, 24)) * t);
        uint r = (uint)(Channel(from, 0) + (Channel(to, 0) - Channel(from, 0)) * t);
        uint g = (uint)(Channel(from, 8) + (Channel(to, 8) - Channel(from, 8)) * t);
        uint b = (uint)(Channel(from, 16) + (Channel(to, 16) - Channel(from, 16)) * t);
        return a << 24 | b << 16 | g << 8 | r;
    }

    /// <summary>Theme color multiplied by an extra alpha.</summary>
    public static uint WithAlpha(uint packed, float alpha)
    {
        uint a = (uint)(((packed >> 24) & 0xFF) * Math.Clamp(alpha, 0f, 1f));
        return (packed & 0x00FFFFFF) | (a << 24);
    }

    /// <summary>
    /// Show a plain tooltip without the varargs entry point.
    /// </summary>
    public static void Tooltip(string text)
    {
        if (ImGui.BeginTooltip() != 0)
        {
            ImGui.TextUnformatted(text);
            ImGui.EndTooltip();
        }
    }

    /// <summary>Tooltip for the previous item.</summary>
    public static void ItemTooltip(string text)
    {
        if (ImGui.IsItemHovered() != 0)
        {
            Tooltip(text);
        }
    }

    /// <summary>Disabled-colored text.</summary>
    public static void TextDisabled(string text) => ImGui.TextUnformatted(text);

    /// <summary>Set the alpha of a packed color.</summary>
    public static uint SetAlpha(uint packed, float alpha)
    {
        uint a = (uint)(255f * Math.Clamp(alpha, 0f, 1f));
        return (packed & 0x00FFFFFF) | (a << 24);
    }
}
