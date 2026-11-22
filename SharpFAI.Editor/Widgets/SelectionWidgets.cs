using ImGui4NET;

namespace SharpFAI.Editor.Widgets;

/// <summary>
/// Segmented control: pick one of several values with a sliding highlight.
/// </summary>
public static unsafe class SegmentedControl
{
    /// <summary>
    /// Draw a segmented control.
    /// </summary>
    /// <param name="id">Unique identifier, usually hidden (e.g. "##viewmode").</param>
    /// <param name="labels">Labels of each segment.</param>
    /// <param name="selected">Currently selected index.</param>
    /// <param name="animate">Whether to animate the highlight.</param>
    /// <returns>True if the selection changed this frame.</returns>
    public static bool Draw(string id, string[] labels, ref int selected, bool animate = true)
    {
        if (labels.Length == 0)
        {
            return false;
        }

        selected = Math.Clamp(selected, 0, labels.Length - 1);

        string key = id;
        if (!Highlights.TryGetValue(key, out float highlight))
        {
            highlight = selected;
            Highlights[key] = highlight;
        }

        float height = ImGui.GetFrameHeight();
        float segmentWidth = 0f;
        foreach (string label in labels)
        {
            segmentWidth = MathF.Max(segmentWidth, ImGui.CalcTextSize(label, null, 0, -1f).x + 24f);
        }

        ImVec2 total = WidgetDraw.V2(segmentWidth * labels.Length, height);
        ImVec2 origin = ImGui.GetCursorScreenPos();
        ImDrawList* draw = ImGui.GetWindowDrawList();

        // Track
        ImVec2 max = WidgetDraw.V2(origin.x + total.x, origin.y + total.y);
        WidgetDraw.Panel(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), height * 0.5f);

        // Sliding highlight
        float target = selected;
        if (animate)
        {
            highlight += (target - highlight) * 0.25f;
            if (MathF.Abs(target - highlight) < 0.005f)
            {
                highlight = target;
            }
        }
        else
        {
            highlight = target;
        }

        Highlights[key] = highlight;

        ImVec2 pillMin = WidgetDraw.V2(origin.x + highlight * segmentWidth + 2f, origin.y + 2f);
        ImVec2 pillMax = WidgetDraw.V2(pillMin.x + segmentWidth - 4f, max.y - 2f);
        WidgetDraw.Panel(draw, in pillMin, in pillMax, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_ButtonActive), (height - 4f) * 0.5f);

        // Labels and hit areas
        bool changed = false;
        for (int i = 0; i < labels.Length; i++)
        {
            ImVec2 cellMin = WidgetDraw.V2(origin.x + i * segmentWidth, origin.y);
            ImVec2 cellMax = WidgetDraw.V2(cellMin.x + segmentWidth, max.y);

            ImGui.SetCursorScreenPos(in cellMin);
            ImGui.PushID(i);
            bool clicked = ImGui.InvisibleButton("##segment", total with { x = segmentWidth }) != 0;
            bool hovered = ImGui.IsItemHovered() != 0;
            ImGui.PopID();

            if (clicked && selected != i)
            {
                selected = i;
                changed = true;
            }

            uint textColor = i == selected
                ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text)
                : WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, hovered ? 0.75f : 0.5f);

            WidgetDraw.TextCentered(draw, in cellMin, in cellMax, textColor, labels[i]);
        }

        // Restore the cursor below the widget
        ImGui.SetCursorScreenPos(in origin);
        ImGui.Dummy(in total);

        return changed;
    }

    private static readonly Dictionary<string, float> Highlights = [];
}

/// <summary>
/// Multi-select tag group with wrapping; handy for filters.
/// </summary>
public static unsafe class TagSelector
{
    /// <summary>
    /// Draw a tag group.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="tags">All tags.</param>
    /// <param name="selected">Selection flags, one per tag.</param>
    /// <returns>True if any tag was toggled this frame.</returns>
    public static bool Draw(string id, string[] tags, bool[] selected)
    {
        if (tags.Length != selected.Length)
        {
            throw new ArgumentException("标签与选中状态数量必须一致 / Tag and selection arrays must have equal length", nameof(selected));
        }

        ImDrawList* draw = ImGui.GetWindowDrawList();
        float height = ImGui.GetFrameHeight() * 0.85f;
        float paddingX = 10f;
        float spacing = 6f;
        float available = ImGui.GetContentRegionAvail().x;

        bool changed = false;
        float x = 0f;

        ImGui.PushID(id);
        for (int i = 0; i < tags.Length; i++)
        {
            float width = ImGui.CalcTextSize(tags[i], null, 0, -1f).x + paddingX * 2f;
            if (x > 0f && x + width > available)
            {
                x = 0f;
                ImGui.NewLine();
            }

            if (x == 0f)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX());
            }
            else
            {
                ImGui.SameLine(0f, spacing);
            }

            ImVec2 origin = ImGui.GetCursorScreenPos();
            ImVec2 size = WidgetDraw.V2(width, height);

            ImGui.PushID(i);
            bool clicked = ImGui.InvisibleButton("##tag", in size) != 0;
            bool hovered = ImGui.IsItemHovered() != 0;
            ImGui.PopID();

            if (clicked)
            {
                selected[i] = !selected[i];
                changed = true;
            }

            ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);
            bool isOn = selected[i];

            uint fill = isOn
                ? WidgetDraw.Blend(WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_ButtonActive), WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrab), hovered ? 0.5f : 0.85f)
                : WidgetDraw.ThemeColor(hovered ? ImGuiCol.ImGuiCol_FrameBgHovered : ImGuiCol.ImGuiCol_FrameBg);

            uint text = isOn
                ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text)
                : WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, hovered ? 0.8f : 0.55f);

            WidgetDraw.Panel(draw, in origin, in max, fill, height * 0.5f);
            WidgetDraw.TextCentered(draw, in origin, in max, text, tags[i]);

            x += width + spacing;
        }

        ImGui.PopID();
        return changed;
    }
}

/// <summary>
/// A clickable color swatch.
/// </summary>
public static unsafe class ColorSwatch
{
    /// <summary>
    /// Draw a color swatch.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="color">Color in RGBA, 0~1 range.</param>
    /// <param name="size">Widget size; zero uses a square of the frame height.</param>
    /// <param name="selected">Whether to draw a selection ring.</param>
    /// <returns>Whether it was clicked.</returns>
    public static bool Draw(string id, in OpenTK.Mathematics.Vector4 color, OpenTK.Mathematics.Vector2 size = default, bool selected = false)
    {
        float height = ImGui.GetFrameHeight();
        float width = size.X > 0f ? size.X : height;
        float h = size.Y > 0f ? size.Y : height;

        ImVec2 origin = ImGui.GetCursorScreenPos();
        ImVec2 dim = WidgetDraw.V2(width, h);

        ImGui.PushID(id);
        bool clicked = ImGui.InvisibleButton("##swatch", in dim) != 0;
        bool hovered = ImGui.IsItemHovered() != 0;
        ImGui.PopID();

        ImVec2 max = WidgetDraw.V2(origin.x + dim.x, origin.y + dim.y);
        ImDrawList* draw = ImGui.GetWindowDrawList();

        // Checkerboard to reveal alpha
        if (color.W < 1f)
        {
            DrawCheckerboard(draw, in origin, in dim, 6f);
        }

        WidgetDraw.Panel(draw, in origin, in max, WidgetDraw.U32(color), 4f);

        uint border = selected
            ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrab)
            : hovered
                ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.5f)
                : WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border);

        WidgetDraw.Outline(draw, in origin, in max, border, 4f, selected ? 2f : 1f);

        if (hovered)
        {
            WidgetDraw.ItemTooltip($"#{ToHex(color)}");
        }

        return clicked;
    }

    private static void DrawCheckerboard(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, float cell)
    {
        uint dark = WidgetDraw.U32(0.35f, 0.35f, 0.35f);
        uint light = WidgetDraw.U32(0.55f, 0.55f, 0.55f);

        int columns = (int)MathF.Ceiling(size.x / cell);
        int rows = (int)MathF.Ceiling(size.y / cell);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                ImVec2 min = WidgetDraw.V2(origin.x + x * cell, origin.y + y * cell);
                ImVec2 max = WidgetDraw.V2(
                    MathF.Min(min.x + cell, origin.x + size.x),
                    MathF.Min(min.y + cell, origin.y + size.y));

                ImDrawList.AddRectFilled(draw, in min, in max, (x + y) % 2 == 0 ? dark : light);
            }
        }
    }

    /// <summary>Format as #RRGGBB.</summary>
    public static string ToHex(in OpenTK.Mathematics.Vector4 color)
    {
        byte r = (byte)(Math.Clamp(color.X, 0f, 1f) * 255f);
        byte g = (byte)(Math.Clamp(color.Y, 0f, 1f) * 255f);
        byte b = (byte)(Math.Clamp(color.Z, 0f, 1f) * 255f);
        return $"{r:X2}{g:X2}{b:X2}";
    }
}
