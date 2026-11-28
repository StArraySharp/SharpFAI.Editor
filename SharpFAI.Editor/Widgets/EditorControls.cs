using ImGui4NET;
using static ImGui4NET.ImGuiCol;

namespace SharpFAI.Editor.Widgets;

internal static unsafe class EditorControls
{
    internal enum ControlIcon
    {
        Restart, Play, Pause, Add, Center, Previous, Next, More, Visible, Hidden, Lock, Unlock, Trash, Headphones, Fit, Loop, Metronome
    }

    internal static float IconButtonWidth => ImGui.GetFrameHeight();

    internal static float ActionWidth(string text) =>
        ImGui.CalcTextSize(text).x + ImGui.GetFrameHeight() + ImGui.GetStyle()->FramePadding.x;

    internal static bool Action(string id, ControlIcon icon, string? text = null, bool emphasized = false,
        bool stretch = false)
    {
        float height = ImGui.GetFrameHeight();
        float desiredWidth = text is null ? IconButtonWidth : ActionWidth(text);
        float available = Math.Max(1, ImGui.GetContentRegionAvail().x);
        float width = stretch ? available : Math.Min(desiredWidth, available);
        if (emphasized) PushSelected();
        bool pressed = ImGui.Button("###" + id, WidgetDraw.V2(width, height)) != 0;
        if (emphasized) ImGui.PopStyleColor();
        if (ImGui.IsItemVisible() == 0) return pressed;

        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        ImDrawList* draw = ImGui.GetWindowDrawList();
        uint color = WidgetDraw.ThemeColor(ImGuiCol_Text);
        float cy = (min.y + max.y) * .5f;
        ImDrawList.PushClipRect(draw, min, max, 1);
        DrawIcon(draw, icon, text is null ? (min.x + max.x) * .5f : min.x + height * .5f, cy, color);
        if (text is not null)
            ImDrawList.AddText(draw, WidgetDraw.V2(min.x + height, cy - ImGui.GetTextLineHeight() * .5f), color, text);
        ImDrawList.PopClipRect(draw);
        if (text is not null && width < desiredWidth) WidgetDraw.ItemTooltip(text);
        return pressed;
    }

    internal static void PushSelected() => ImGui.PushStyleColor((int)ImGuiCol_Button,
        *ImGui.GetStyleColorVec4((int)ImGuiCol_Header));

    internal static bool Toggle(string label, ref bool value, bool stretch = false)
    {
        if (value) PushSelected();
        bool pressed = ImGui.Button(label, stretch ? WidgetDraw.V2(-1, 0) : default) != 0;
        if (value) ImGui.PopStyleColor();
        if (pressed) value = !value;
        return pressed;
    }

    internal static float ReadoutWidth(string text) =>
        ImGui.CalcTextSize(text).x + 2 * ImGui.GetStyle()->FramePadding.x;

    internal static void Readout(string text, bool accent = false)
    {
        float desiredWidth = ReadoutWidth(text);
        float width = Math.Clamp(desiredWidth, 1, Math.Max(1, ImGui.GetContentRegionAvail().x));
        ImGui.Dummy(WidgetDraw.V2(width, ImGui.GetFrameHeight()));
        if (ImGui.IsItemVisible() == 0) return;
        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        ImDrawList* draw = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        WidgetDraw.Panel(draw, min, max, WidgetDraw.ThemeColor(ImGuiCol_ChildBg), style->FrameRounding);
        ImDrawList.PushClipRect(draw, min, max, 1);
        ImDrawList.AddText(draw, WidgetDraw.V2(min.x + style->FramePadding.x, min.y + style->FramePadding.y),
            WidgetDraw.ThemeColor(accent ? ImGuiCol_CheckMark : ImGuiCol_TextDisabled), text);
        ImDrawList.PopClipRect(draw);
        if (width < desiredWidth) WidgetDraw.ItemTooltip(text);
    }

    internal static bool EventTile(string id, string text, EventPaletteIcons.Icon? icon, bool selected, float side)
    {
        if (selected) PushSelected();
        bool pressed = ImGui.Button("###" + id, WidgetDraw.V2(side, side)) != 0;
        if (selected) ImGui.PopStyleColor();
        if (ImGui.IsItemVisible() == 0) return pressed;

        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        ImVec2 padding = ImGui.GetStyle()->FramePadding;
        float left = min.x + padding.x, right = max.x - padding.x;
        float centerY = (min.y + max.y) * .5f;
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImDrawList.PushClipRect(draw, min, max, 1);
        if (icon is { } sprite)
        {
            float iconSide = side - 4;
            if (iconSide > 0)
            {
                float scale = iconSide / Math.Max(sprite.Width, sprite.Height);
                float halfWidth = sprite.Width * scale * .5f, halfHeight = sprite.Height * scale * .5f;
                float centerX = (min.x + max.x) * .5f;
                ImDrawList.AddImage(draw, new ImTextureRef { _TexID = (uint)sprite.Texture },
                    WidgetDraw.V2(centerX - halfWidth, centerY - halfHeight),
                    WidgetDraw.V2(centerX + halfWidth, centerY + halfHeight),
                    WidgetDraw.V2(0, 0), WidgetDraw.V2(1, 1),
                    WidgetDraw.WithAlpha(uint.MaxValue, ImGui.GetStyle()->Alpha));
            }
            ImDrawList.PopClipRect(draw);
            return pressed;
        }
        if (right > left)
        {
            float width = right - left;
            ImVec2 textSize = ImGui.CalcTextSize(text, null, 0, width);
            float top = Math.Max(min.y, centerY - textSize.y * .5f);
            ImDrawList.PushClipRect(draw, WidgetDraw.V2(left, min.y), WidgetDraw.V2(right, max.y), 1);
            WidgetDraw.TextWrapped(draw, WidgetDraw.V2(left, top), width,
                WidgetDraw.ThemeColor(ImGuiCol_Text), text);
            ImDrawList.PopClipRect(draw);
        }
        ImDrawList.PopClipRect(draw);
        return pressed;
    }

    private static void DrawIcon(ImDrawList* draw, ControlIcon icon, float x, float y, uint color)
    {
        float scale = ImGui.GetFontSize() / 17f;
        float stroke = Math.Max(1, 1.5f * scale);
        ImVec2 Point(float dx, float dy) => WidgetDraw.V2(x + dx * scale, y + dy * scale);
        void Line(float x1, float y1, float x2, float y2) =>
            ImDrawList.AddLine(draw, Point(x1, y1), Point(x2, y2), color, stroke);
        void Box(float x1, float y1, float x2, float y2) =>
            ImDrawList.AddRect(draw, Point(x1, y1), Point(x2, y2), color, scale, stroke);

        switch (icon)
        {
            case ControlIcon.Restart:
                Line(-5.5f, -5, -5.5f, 5);
                ImDrawList.AddTriangleFilled(draw, Point(4, -5), Point(-3.5f, 0), Point(4, 5), color);
                break;
            case ControlIcon.Play:
                ImDrawList.AddTriangleFilled(draw, Point(-3.5f, -5.5f), Point(5.5f, 0), Point(-3.5f, 5.5f), color);
                break;
            case ControlIcon.Pause:
                Line(-2.5f, -5, -2.5f, 5);
                Line(2.5f, -5, 2.5f, 5);
                break;
            case ControlIcon.Headphones:
                ImDrawList.AddBezierCubic(draw, Point(-5.5f, 0), Point(-5.5f, -7.5f),
                    Point(5.5f, -7.5f), Point(5.5f, 0), color, stroke, 12);
                Box(-6.5f, 0, -3.5f, 4.5f);
                Box(3.5f, 0, 6.5f, 4.5f);
                break;
            case ControlIcon.Add:
                Line(-5, 0, 5, 0);
                Line(0, -5, 0, 5);
                break;
            case ControlIcon.Center:
                ImDrawList.AddCircle(draw, Point(0, 0), 4.5f * scale, color, 16, stroke);
                Line(-7, 0, -4, 0); Line(4, 0, 7, 0);
                Line(0, -7, 0, -4); Line(0, 4, 0, 7);
                break;
            case ControlIcon.Fit:
                Line(-6.5f, -5, -6.5f, 5); Line(6.5f, -5, 6.5f, 5);
                Line(-4.5f, 0, 4.5f, 0);
                Line(-4.5f, 0, -2, -2.5f); Line(-4.5f, 0, -2, 2.5f);
                Line(4.5f, 0, 2, -2.5f); Line(4.5f, 0, 2, 2.5f);
                break;
            case ControlIcon.Metronome:
                Line(-5.5f, 5.5f, -2.5f, -5.5f); Line(-2.5f, -5.5f, 2.5f, -5.5f);
                Line(2.5f, -5.5f, 5.5f, 5.5f); Line(5.5f, 5.5f, -5.5f, 5.5f);
                Line(-1, 3, 5, -4);
                ImDrawList.AddCircleFilled(draw, Point(3.5f, -2.25f), 1.45f * scale, color, 10);
                break;
            case ControlIcon.Loop:
                Line(-3.5f, -3.5f, 6, -3.5f);
                Line(6, -3.5f, 3.5f, -6); Line(6, -3.5f, 3.5f, -1);
                Line(3.5f, 3.5f, -6, 3.5f);
                Line(-6, 3.5f, -3.5f, 1); Line(-6, 3.5f, -3.5f, 6);
                ImDrawList.AddBezierCubic(draw, Point(-3.5f, -3.5f), Point(-5, -3.5f),
                    Point(-6, -2), Point(-6, -0.5f), color, stroke, 6);
                ImDrawList.AddBezierCubic(draw, Point(3.5f, 3.5f), Point(5, 3.5f),
                    Point(6, 2), Point(6, 0.5f), color, stroke, 6);
                break;
            case ControlIcon.Previous:
                Line(2, -4.5f, -3, 0); Line(-3, 0, 2, 4.5f);
                break;
            case ControlIcon.Next:
                Line(-2, -4.5f, 3, 0); Line(3, 0, -2, 4.5f);
                break;
            case ControlIcon.More:
                for (int i = -1; i <= 1; i++)
                    ImDrawList.AddCircleFilled(draw, Point(i * 4, 0), 1.1f * scale, color, 8);
                break;
            case ControlIcon.Visible:
            case ControlIcon.Hidden:
                Line(-7, 0, -3, -3.5f); Line(-3, -3.5f, 3, -3.5f); Line(3, -3.5f, 7, 0);
                Line(7, 0, 3, 3.5f); Line(3, 3.5f, -3, 3.5f); Line(-3, 3.5f, -7, 0);
                ImDrawList.AddCircleFilled(draw, Point(0, 0), 1.6f * scale, color, 10);
                if (icon == ControlIcon.Hidden) Line(-6, 5.5f, 6, -5.5f);
                break;
            case ControlIcon.Lock:
            case ControlIcon.Unlock:
                Box(-4.5f, -1, 4.5f, 5.5f);
                Line(-2.5f, -1, -2.5f, -4);
                Line(-2.5f, -4, 0, -6); Line(0, -6, 2.5f, -4);
                if (icon == ControlIcon.Lock) Line(2.5f, -4, 2.5f, -1);
                break;
            case ControlIcon.Trash:
                Line(-5.5f, -3, 5.5f, -3); Line(-2, -5.5f, 2, -5.5f);
                Box(-3.5f, -2, 3.5f, 5);
                break;
        }
    }
}
