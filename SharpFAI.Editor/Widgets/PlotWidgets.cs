using ImGui4NET;
using OpenTK.Mathematics;

namespace SharpFAI.Editor.Widgets;

/// <summary>
/// Channel configuration for <see cref="CurveEditor"/>.
/// </summary>
public sealed class CurveChannel
{
    public CurveChannel(string name, Vector4 color, float[] keys)
    {
        Name = name;
        Color = color;
        Keys = keys;
    }

    public string Name { get; }

    public Vector4 Color { get; }

    /// <summary>Key values, evenly spaced over the visible range.</summary>
    public float[] Keys { get; set; }

    public bool Visible { get; set; } = true;
}

/// <summary>
/// Curve editor with multiple channels, grid, auto range and hovered-value readout.
/// </summary>
public static unsafe class CurveEditor
{
    /// <summary>
    /// Draw the curve editor.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="channels">Channels to draw.</param>
    /// <param name="height">Widget height, 0 for the default.</param>
    /// <param name="interactive">Whether the mouse readout is enabled.</param>
    public static void Draw(string id, CurveChannel[] channels, float height = 0f, bool interactive = true)
    {
        if (height <= 0f)
        {
            height = ImGui.GetFrameHeight() * 6f;
        }

        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(avail.x, height);
        ImVec2 origin = ImGui.GetCursorScreenPos();

        ImGui.PushID(id);
        ImGui.InvisibleButton("##canvas", in size);
        bool hovered = ImGui.IsItemHovered() != 0;

        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);

        WidgetDraw.Panel(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 6f);

        // Work out the value range
        (float rangeMin, float rangeMax) = ComputeRange(channels);
        if (rangeMax - rangeMin < 1e-6f)
        {
            rangeMax = rangeMin + 1f;
        }

        DrawGrid(draw, in origin, in size, rangeMin, rangeMax);

        // One polyline per channel
        foreach (CurveChannel channel in channels)
        {
            if (!channel.Visible || channel.Keys.Length < 2)
            {
                continue;
            }

            DrawCurve(draw, in origin, in size, rangeMin, rangeMax, channel);
        }

        DrawLegend(draw, in origin, in size, channels);

        // Hovered cursor
        if (interactive && hovered)
        {
            DrawCursor(draw, in origin, in size, rangeMin, rangeMax, channels);
        }

        WidgetDraw.Outline(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 6f);
        ImGui.PopID();
    }

    private static (float Min, float Max) ComputeRange(CurveChannel[] channels)
    {
        float min = float.MaxValue;
        float max = float.MinValue;

        foreach (CurveChannel channel in channels)
        {
            if (!channel.Visible)
            {
                continue;
            }

            foreach (float key in channel.Keys)
            {
                min = MathF.Min(min, key);
                max = MathF.Max(max, key);
            }
        }

        if (min > max)
        {
            return (0f, 1f);
        }

        float padding = (max - min) * 0.1f;
        return (min - padding, max + padding);
    }

    private static void DrawGrid(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, float min, float max)
    {
        uint grid = WidgetDraw.WithAlpha(WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 0.7f);
        const int divisions = 4;

        for (int i = 0; i <= divisions; i++)
        {
            float t = (float)i / divisions;
            float y = origin.y + size.y * t;
            ImVec2 a = WidgetDraw.V2(origin.x, y);
            ImVec2 b = WidgetDraw.V2(origin.x + size.x, y);
            ImDrawList.AddLine(draw, in a, in b, grid, 1f);
        }

        for (int i = 0; i <= divisions; i++)
        {
            float t = (float)i / divisions;
            float x = origin.x + size.x * t;
            ImVec2 a = WidgetDraw.V2(x, origin.y);
            ImVec2 b = WidgetDraw.V2(x, origin.y + size.y);
            ImDrawList.AddLine(draw, in a, in b, grid, 1f);
        }

        // Range labels
        uint labelColor = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.45f);
        ImVec2 topLeft = WidgetDraw.V2(origin.x + 6f, origin.y + 4f);
        ImVec2 bottomLeft = WidgetDraw.V2(origin.x + 6f, origin.y + size.y - ImGui.GetTextLineHeight() - 4f);
        ImDrawList.AddText(draw, in topLeft, labelColor, max.ToString("0.##"));
        ImDrawList.AddText(draw, in bottomLeft, labelColor, min.ToString("0.##"));
    }

    private static void DrawCurve(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, float min, float max, CurveChannel channel)
    {
        float range = max - min;
        int count = channel.Keys.Length;

        for (int i = 0; i + 1 < count; i++)
        {
            ImVec2 a = WidgetDraw.V2(
                origin.x + size.x * (i / (float)(count - 1)),
                origin.y + size.y * (1f - (channel.Keys[i] - min) / range));

            ImVec2 b = WidgetDraw.V2(
                origin.x + size.x * ((i + 1) / (float)(count - 1)),
                origin.y + size.y * (1f - (channel.Keys[i + 1] - min) / range));

            ImDrawList.AddLine(draw, in a, in b, WidgetDraw.U32(channel.Color), 2f);
        }

        // Keyframe dots
        for (int i = 0; i < count; i++)
        {
            ImVec2 p = WidgetDraw.V2(
                origin.x + size.x * (i / (float)(count - 1)),
                origin.y + size.y * (1f - (channel.Keys[i] - min) / range));

            ImDrawList.AddCircleFilled(draw, in p, 2.5f, WidgetDraw.U32(channel.Color));
        }
    }

    private static void DrawLegend(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, CurveChannel[] channels)
    {
        float x = origin.x + size.x - 8f;
        float y = origin.y + 6f;
        float lineHeight = ImGui.GetTextLineHeight();

        for (int i = channels.Length - 1; i >= 0; i--)
        {
            CurveChannel channel = channels[i];
            ImVec2 textSize = ImGui.CalcTextSize(channel.Name, null, 0, -1f);
            x -= textSize.x;
            ImVec2 pos = WidgetDraw.V2(x, y + i * lineHeight);

            uint color = channel.Visible ? WidgetDraw.U32(channel.Color) : WidgetDraw.WithAlpha(WidgetDraw.U32(channel.Color), 0.35f);
            ImDrawList.AddText(draw, in pos, color, channel.Name);
            x -= 10f;

            ImVec2 dot = WidgetDraw.V2(x + 3f, pos.y + lineHeight * 0.5f);
            ImDrawList.AddCircleFilled(draw, in dot, 3f, color);
            x -= 12f;
        }
    }

    private static void DrawCursor(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, float min, float max, CurveChannel[] channels)
    {
        ImVec2 mouse = ImGui.GetMousePos();
        float t = Math.Clamp((mouse.x - origin.x) / size.x, 0f, 1f);

        ImVec2 top = WidgetDraw.V2(origin.x + size.x * t, origin.y);
        ImVec2 bottom = WidgetDraw.V2(top.x, origin.y + size.y);
        ImDrawList.AddLine(draw, in top, in bottom, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.35f), 1f);

        float range = max - min;
        float textY = origin.y + 4f;

        foreach (CurveChannel channel in channels)
        {
            if (!channel.Visible || channel.Keys.Length < 2)
            {
                continue;
            }

            float value = SampleChannel(channel, t);
            float y = origin.y + size.y * (1f - (value - min) / range);

            ImVec2 point = WidgetDraw.V2(top.x, y);
            ImDrawList.AddCircleFilled(draw, in point, 3.5f, WidgetDraw.U32(channel.Color));

            string label = value.ToString("0.###");
            ImVec2 labelPos = WidgetDraw.V2(top.x + 6f, textY);
            ImDrawList.AddText(draw, in labelPos, WidgetDraw.U32(channel.Color), label);
            textY += ImGui.GetTextLineHeight();
        }
    }

    /// <summary>Linearly sample a channel at a normalized position.</summary>
    private static float SampleChannel(CurveChannel channel, float t)
    {
        int count = channel.Keys.Length;
        float position = t * (count - 1);
        int index = Math.Clamp((int)MathF.Floor(position), 0, count - 1);
        int next = Math.Min(index + 1, count - 1);
        float fraction = position - index;

        return WidgetDraw.SmoothStep(fraction) is var smooth && smooth > 0f
            ? channel.Keys[index] + (channel.Keys[next] - channel.Keys[index]) * fraction
            : channel.Keys[index];
    }
}

/// <summary>
/// Histogram plot.
/// </summary>
public static unsafe class Histogram
{
    /// <summary>
    /// Draw a histogram.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="values">Bar values.</param>
    /// <param name="color">Bar color.</param>
    /// <param name="height">Height, 0 for the default.</param>
    /// <param name="showLabels">Whether to draw numeric labels.</param>
    public static void Draw(string id, ReadOnlySpan<float> values, Vector4 color, float height = 0f, bool showLabels = false)
    {
        if (values.Length == 0)
        {
            return;
        }

        if (height <= 0f)
        {
            height = ImGui.GetFrameHeight() * 4f;
        }

        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(avail.x, height);
        ImVec2 origin = ImGui.GetCursorScreenPos();

        ImGui.PushID(id);
        ImGui.InvisibleButton("##hist", in size);
        bool hovered = ImGui.IsItemHovered() != 0;

        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);

        WidgetDraw.Panel(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 4f);

        float peak = 0f;
        foreach (float value in values)
        {
            peak = MathF.Max(peak, MathF.Abs(value));
        }

        if (peak < 1e-6f)
        {
            peak = 1f;
        }

        float slot = size.x / values.Length;
        float gap = MathF.Min(slot * 0.15f, 3f);
        uint barColor = WidgetDraw.U32(color);

        for (int i = 0; i < values.Length; i++)
        {
            float ratio = MathF.Abs(values[i]) / peak;
            float barHeight = (size.y - 4f) * ratio;

            ImVec2 barMin = WidgetDraw.V2(origin.x + i * slot + gap, origin.y + size.y - 2f - barHeight);
            ImVec2 barMax = WidgetDraw.V2(barMin.x + slot - gap * 2f, origin.y + size.y - 2f);

            ImDrawList.AddRectFilled(draw, in barMin, in barMax, barColor, 2f);
        }

        // Highlight the hovered bar
        if (hovered)
        {
            ImVec2 mouse = ImGui.GetMousePos();
            int index = Math.Clamp((int)((mouse.x - origin.x) / slot), 0, values.Length - 1);

            ImVec2 barMin = WidgetDraw.V2(origin.x + index * slot + gap, origin.y + 2f);
            ImVec2 barMax = WidgetDraw.V2(barMin.x + slot - gap * 2f, origin.y + size.y - 2f);
            ImDrawList.AddRectFilled(draw, in barMin, in barMax, WidgetDraw.WithAlpha(barColor, 0.25f), 2f);

            if (showLabels)
            {
                WidgetDraw.Tooltip($"[{index}] {values[index]:0.###}");
            }
            else
            {
                WidgetDraw.ItemTooltip($"[{index}] {values[index]:0.###}");
            }
        }

        WidgetDraw.Outline(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 4f);
        ImGui.PopID();
    }
}

/// <summary>
/// Circular progress ring.
/// </summary>
public static unsafe class ProgressRing
{
    /// <summary>
    /// Draw a progress ring.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="fraction">Progress in 0~1.</param>
    /// <param name="radius">Radius in pixels.</param>
    /// <param name="thickness">Ring thickness.</param>
    /// <param name="color">Progress color.</param>
    /// <param name="caption">Text drawn in the middle.</param>
    public static void Draw(string id, float fraction, float radius = 26f, float thickness = 5f, Vector4 color = default, string? caption = null)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        if (color == default)
        {
            color = Vector4.One;
        }

        float diameter = radius * 2f;
        ImVec2 origin = ImGui.GetCursorScreenPos();
        ImGui.PushID(id);

        ImGui.InvisibleButton("##ring", WidgetDraw.V2(diameter, diameter));
        bool hovered = ImGui.IsItemHovered() != 0;
        ImGui.PopID();

        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 center = WidgetDraw.V2(origin.x + radius, origin.y + radius);

        // Track
        ImDrawList.AddCircle(draw, in center, radius, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 48, thickness);

        // Progress arc approximated with short segments
        const int segments = 48;
        int steps = (int)MathF.Ceiling(segments * fraction);
        uint arcColor = WidgetDraw.U32(color);

        for (int i = 0; i < steps; i++)
        {
            float t0 = i / (float)segments;
            float t1 = MathF.Min((i + 1) / (float)segments, fraction);

            // Starts at 12 o'clock and goes clockwise
            float a0 = -MathF.PI / 2f + t0 * MathF.PI * 2f;
            float a1 = -MathF.PI / 2f + t1 * MathF.PI * 2f;

            ImVec2 p0 = WidgetDraw.V2(center.x + MathF.Cos(a0) * radius, center.y + MathF.Sin(a0) * radius);
            ImVec2 p1 = WidgetDraw.V2(center.x + MathF.Cos(a1) * radius, center.y + MathF.Sin(a1) * radius);
            ImDrawList.AddLine(draw, in p0, in p1, arcColor, thickness);
        }

        string text = caption ?? $"{fraction * 100f:0}%";
        ImVec2 textSize = ImGui.CalcTextSize(text, null, 0, -1f);
        ImVec2 textPos = WidgetDraw.V2(center.x - textSize.x * 0.5f, center.y - textSize.y * 0.5f);
        ImDrawList.AddText(draw, in textPos, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text), text);

        if (hovered)
        {
            WidgetDraw.ItemTooltip($"{fraction * 100f:0.##}%");
        }
    }
}
