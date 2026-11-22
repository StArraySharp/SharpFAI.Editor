using ImGui4NET;

namespace SharpFAI.Editor.Widgets;

/// <summary>
/// Tuning options for <see cref="NumericDrag"/>.
/// </summary>
public sealed class NumericDragOptions
{
    /// <summary>Value change per pixel dragged.</summary>
    public float Speed { get; set; } = 0.01f;

    /// <summary>Minimum value.</summary>
    public float Min { get; set; } = float.MinValue;

    /// <summary>Maximum value.</summary>
    public float Max { get; set; } = float.MaxValue;

    /// <summary>Multiplier while Shift is held (fine tuning).</summary>
    public float SlowMultiplier { get; set; } = 0.1f;

    /// <summary>Multiplier while Ctrl is held (coarse tuning).</summary>
    public float FastMultiplier { get; set; } = 10f;

    /// <summary>Display format.</summary>
    public string Format { get; set; } = "0.###";

    /// <summary>Enter text-edit mode on double click.</summary>
    public bool AllowTextInput { get; set; } = true;

    /// <summary>Reset to <see cref="DefaultValue"/> on right click.</summary>
    public bool ResetOnRightClick { get; set; }

    /// <summary>Value used when resetting.</summary>
    public float DefaultValue { get; set; }

    /// <summary>Revert the drag when Escape is pressed.</summary>
    public bool CancelOnEscape { get; set; } = true;
}

/// <summary>
/// Draggable numeric field with fine/coarse modifiers, inline editing and arrow-key nudging.
/// </summary>
public static unsafe class NumericDrag
{
    private const int TextBufferSize = 128;
    private static readonly Dictionary<string, DragState> States = [];
    private static readonly byte[] TextBuffer = new byte[TextBufferSize];

    private sealed class DragState
    {
        public bool Dragging;
        public bool Editing;
        public float StartValue;
    }

    /// <summary>
    /// Draw a draggable numeric field.
    /// </summary>
    /// <param name="id">Unique id, also used as the label.</param>
    /// <param name="value">Current value.</param>
    /// <param name="options">Behaviour options.</param>
    /// <returns>True when the value changed this frame.</returns>
    public static bool Draw(string id, ref float value, NumericDragOptions? options = null)
    {
        options ??= new NumericDragOptions();
        DragState state = GetState(id);

        ImVec2 origin = ImGui.GetCursorScreenPos();

        if (state.Editing)
        {
            return DrawEditor(id, ref value, options);
        }

        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(avail.x, ImGui.GetFrameHeight());

        ImGui.PushID(id);
        ImGui.InvisibleButton("##drag", in size, (int)ImGuiButtonFlags.ImGuiButtonFlags_MouseButtonLeft);
        bool hovered = ImGui.IsItemHovered() != 0;
        bool active = ImGui.IsItemActive() != 0;
        bool clicked = ImGui.IsItemClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0;
        bool rightClicked = ImGui.IsItemClicked((int)ImGuiMouseButton.ImGuiMouseButton_Right) != 0;
        bool doubleClicked = ImGui.IsMouseDoubleClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0;
        ImGui.PopID();

        bool changed = false;

        if (rightClicked && options.ResetOnRightClick)
        {
            float reset = Math.Clamp(options.DefaultValue, options.Min, options.Max);
            if (!reset.Equals(value))
            {
                value = reset;
                changed = true;
            }

            state.Dragging = false;
            DrawField(options, value, hovered, false, in origin, in size);
            return changed;
        }

        if (doubleClicked && hovered && options.AllowTextInput)
        {
            state.Editing = true;
            state.Dragging = false;
            WriteTextBuffer(value, options.Format);
            ImGui.SetKeyboardFocusHere(-1);
            return false;
        }

        if (clicked)
        {
            state.Dragging = true;
            state.StartValue = value;
        }

        if (state.Dragging && active)
        {
            ImVec2 delta = ImGui.GetMouseDragDelta((int)ImGuiMouseButton.ImGuiMouseButton_Left);
            float speed = options.Speed;

            if (ImGui.GetIO()->KeyShift != 0)
            {
                speed *= options.SlowMultiplier;
            }
            else if (ImGui.GetIO()->KeyCtrl != 0)
            {
                speed *= options.FastMultiplier;
            }

            float next = Math.Clamp(state.StartValue + delta.x * speed, options.Min, options.Max);
            if (!next.Equals(value))
            {
                value = next;
                changed = true;
            }

            ImGui.SetMouseCursor((int)ImGuiMouseCursor.ImGuiMouseCursor_ResizeEW);
        }
        else if (state.Dragging)
        {
            if (options.CancelOnEscape && ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape) != 0)
            {
                value = state.StartValue;
                changed = true;
            }

            state.Dragging = false;
            ImGui.ResetMouseDragDelta((int)ImGuiMouseButton.ImGuiMouseButton_Left);
        }

        if (hovered && !state.Dragging)
        {
            float step = options.Speed * (ImGui.GetIO()->KeyShift != 0 ? options.SlowMultiplier : 1f);

            if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_UpArrow) != 0)
            {
                value = Math.Clamp(value + step, options.Min, options.Max);
                changed = true;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_DownArrow) != 0)
            {
                value = Math.Clamp(value - step, options.Min, options.Max);
                changed = true;
            }
        }

        DrawField(options, value, hovered, state.Dragging, in origin, in size);

        if (hovered && !state.Dragging)
        {
            WidgetDraw.ItemTooltip("拖动改值 · Shift 精调 · Ctrl 快调 · 双击输入 · ↑↓ 微调");
        }

        return changed;
    }

    /// <summary>Paint the field background, value and hints.</summary>
    private static void DrawField(NumericDragOptions options, float value, bool hovered, bool dragging, in ImVec2 origin, in ImVec2 size)
    {
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);

        float t = dragging ? 1f : hovered ? 0.55f : 0f;
        uint background = WidgetDraw.Blend(
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg),
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBgActive),
            t);

        uint border = WidgetDraw.Blend(
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border),
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrab),
            t);

        WidgetDraw.Panel(draw, in origin, in max, background, 4f);
        WidgetDraw.Outline(draw, in origin, in max, border, 4f);

        // Relative-position bar while dragging
        if (dragging && options.Max > options.Min
            && !float.IsInfinity(options.Max - options.Min)
            && (options.Max - options.Min) > 1e-6f)
        {
            float ratio = Math.Clamp((value - options.Min) / (options.Max - options.Min), 0f, 1f);
            ImVec2 barMin = WidgetDraw.V2(origin.x + 2f, max.y - 4f);
            ImVec2 barMax = WidgetDraw.V2(origin.x + 2f + (size.x - 4f) * ratio, max.y - 2f);
            ImDrawList.AddRectFilled(draw, in barMin, in barMax, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrab, 0.9f), 1f);
        }

        string text = value.ToString(options.Format);
        ImVec2 textSize = ImGui.CalcTextSize(text, null, 0, -1f);
        ImVec2 textPos = WidgetDraw.V2(
            origin.x + (size.x - textSize.x) * 0.5f,
            origin.y + (size.y - textSize.y) * 0.5f);

        ImDrawList.AddText(draw, in textPos, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text), text);

        // Small arrows hinting that the field is draggable
        uint hint = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, hovered ? 0.55f : 0.2f);
        float cy = origin.y + size.y * 0.5f;

        ImDrawList.AddTriangleFilled(draw,
            WidgetDraw.V2(origin.x + 6f, cy - 3f),
            WidgetDraw.V2(origin.x + 6f, cy + 3f),
            WidgetDraw.V2(origin.x + 10f, cy), hint);

        ImDrawList.AddTriangleFilled(draw,
            WidgetDraw.V2(max.x - 6f, cy - 3f),
            WidgetDraw.V2(max.x - 6f, cy + 3f),
            WidgetDraw.V2(max.x - 10f, cy), hint);
    }

    /// <summary>Inline editor shown after a double click.</summary>
    private static bool DrawEditor(string id, ref float value, NumericDragOptions options)
    {
        DragState state = GetState(id);

        ImGui.PushID(id);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().x);

        bool committed = false;
        bool changed = false;

        fixed (byte* buffer = TextBuffer)
        {
            committed = ImGui.InputText("##input", (sbyte*)buffer, TextBufferSize,
                (int)ImGuiInputTextFlags.ImGuiInputTextFlags_EnterReturnsTrue) != 0;

            if (committed)
            {
                string text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
                if (float.TryParse(text, out float parsed))
                {
                    float clamped = Math.Clamp(parsed, options.Min, options.Max);
                    if (!clamped.Equals(value))
                    {
                        value = clamped;
                        changed = true;
                    }
                }
            }
        }

        // Leave edit mode on focus loss or Escape
        if (ImGui.IsItemDeactivated() != 0 || ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape) != 0)
        {
            state.Editing = false;
        }

        ImGui.PopID();
        return changed;
    }

    private static void WriteTextBuffer(float value, string format)
    {
        Array.Clear(TextBuffer);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value.ToString(format));
        Array.Copy(bytes, TextBuffer, Math.Min(bytes.Length, TextBuffer.Length - 1));
    }

    private static DragState GetState(string id)
    {
        if (!States.TryGetValue(id, out DragState? state))
        {
            state = new DragState();
            States[id] = state;
        }

        return state;
    }
}
