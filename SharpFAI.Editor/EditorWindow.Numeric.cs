using System.Globalization;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private sealed class NumericDraft(EditorDocument document, JObject target, EditorField field, int component, string text, bool gesture = false)
    {
        public EditorDocument Document { get; } = document;
        public JObject Target { get; } = target;
        public EditorField Field { get; } = field;
        public int Component { get; } = component;
        public string Text { get; set; } = text;
        public bool Changed { get; set; }
        public bool FocusText { get; set; }
        public bool Dragged { get; set; }
        public bool Gesture { get; } = gesture;
        public EditorPropertyEdit Binding { get; } = EditorPropertyEdit.Capture(document, target, field.Key);
        public int Version { get; set; } = document.ChangeVersion;
        public object Group { get; } = new();

        public bool Matches(JObject other, string key, int axis) =>
            Field.Key == key && Component == axis && (ReferenceEquals(Target, other) ||
                Binding.Matches(EditorPropertyEdit.Capture(Document, other, key)));
    }

    private NumericDraft? _numericDraft;
    private NumericDraft? _invalidNumber;
    private bool _numericDrawn;

    private static readonly string[] VectorAxes = ["X", "Y"];
    private static readonly string[] VectorRangeAxes = ["Min X", "Min Y", "Max X", "Max Y"];

    private void DrawVectorInput(JObject target, EditorField field, JArray? value)
    {
        bool range = field.Type == "Vector2Range";
        string[] labels = range ? VectorRangeAxes : VectorAxes;
        float gap = ImGui.GetStyle()->ItemSpacing.x;
        float labelWidth = 0;
        foreach (string label in labels)
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(label).x);
        float numberOffset = labelWidth + ImGui.GetStyle()->ItemInnerSpacing.x;
        float available = ImGui.GetContentRegionAvail().x;
        float half = MathF.Floor((available - gap) * .5f);
        bool sideBySide = half >= numberOffset + 52 * _uiScale;
        float columnWidth = sideBySide ? half : available;
        float left = ImGui.GetCursorPosX();

        for (int index = 0; index < labels.Length; index++)
        {
            int axis = index % 2;
            float start = left;
            if (sideBySide && axis == 1)
            {
                start += columnWidth + gap;
                ImGui.SameLine(start, 0);
            }
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(labels[index]);
            ImGui.SameLine(start + numberOffset, 0);
            JArray? pair = range ? value?.ElementAtOrDefault(index / 2) as JArray : value;
            DrawNumericInput(target, field, pair?.ElementAtOrDefault(axis), index, width: columnWidth - numberOffset);
        }
    }

    private void DrawNumericInput(JObject target, EditorField field, JToken? value, int component = -1, bool step = false,
        float? width = null)
    {
        var current = _numericDraft is { } draft && ReferenceEquals(draft.Document, _document) &&
            draft.Matches(target, field.Key, component) ? draft : null;
        if (current is not null && current.Version != _document.ChangeVersion)
            current = _numericDraft = null;
        var invalid = _invalidNumber is { } error && ReferenceEquals(error.Document, _document) &&
            error.Version == _document.ChangeVersion && error.Matches(target, field.Key, component) ? error : null;
        string content = invalid?.Text ?? (value is JValue scalar
            ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? "" : "");
        ImGui.PushID(component);
        float available = width ?? ImGui.GetContentRegionAvail().x;
        float buttonWidth = ImGui.GetFrameHeight();
        float stepWidth = 2 * (buttonWidth + ImGui.GetStyle()->ItemSpacing.x);
        step &= available >= stepWidth + 52 * _uiScale;
        ImGui.SetNextItemWidth(Math.Max(1, available - (step ? stepWidth : 0)));
        if (component < 0 && !field.AllowEmpty && current is not { Gesture: false })
            DrawInlineNumberDrag(target, field, value, content, current, invalid is not null);
        else
            DrawNumberText(target, field, component, content, current);
        if (step)
        {
            ImGui.SameLine();
            bool minus = ImGui.Button("−", WidgetDraw.V2(buttonWidth, buttonWidth)) != 0;
            ImGui.SameLine();
            bool plus = ImGui.Button("+", WidgetDraw.V2(buttonWidth, buttonWidth)) != 0;
            if (minus || plus)
            {
                FinishNumericEdit();
                var binding = EditorPropertyEdit.Capture(_document, target, field.Key);
                JToken? stored = binding.Read() ?? value;
                double next = EditorPropertyEdit.ClampNumericDrag(field, Number(stored) + (plus ? 1 : -1));
                CommitProperty(binding, new JValue((int)next));
                _invalidNumber = null;
            }
        }
        ImGui.PopID();
    }

    private void DrawNumberText(JObject target, EditorField field, int component, string content, NumericDraft? current)
    {
        content = current?.Text ?? content;
        bool focusing = current?.FocusText == true;
        if (focusing)
        {
            current!.FocusText = false;
            ImGui.SetKeyboardFocusHere();
        }
        string? update = EditText("##number", field.Unit ?? "", content, live: true,
            extraFlags: (int)ImGuiInputTextFlags.ImGuiInputTextFlags_AutoSelectAll);
        bool active = ImGui.IsItemActive() != 0;
        bool released = ImGui.IsItemDeactivated() != 0;
        bool confirm = ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Enter, 0) != 0 ||
            ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_KeypadEnter, 0) != 0;
        bool cancel = ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0;
        if ((active || update is not null) && current is null)
        {
            FinishNumericEdit();
            current = _numericDraft = new NumericDraft(_document, target, field, component, content);
            _invalidNumber = null;
        }
        if (current is not null)
        {
            _numericDrawn = true;
            if (update is not null)
            {
                current.Text = update;
                current.Changed = true;
            }
            if (cancel)
            {
                _numericDraft = null;
                _invalidNumber = null;
            }
            else if (confirm || released || (!active && !focusing))
                FinishNumericEdit();
        }
        if (_invalidNumber?.Matches(target, field.Key, component) == true && ImGui.IsItemHovered() != 0)
            ImGui.SetTooltip(L.T("InvalidNumber"));
    }

    private void DrawInlineNumberDrag(JObject target, EditorField field, JToken? value, string text,
        NumericDraft? current, bool invalid)
    {
        double number = Number(value);
        bool whole = field.Type == "Int";
        const int flags = (int)ImGuiSliderFlags.ImGuiSliderFlags_NoInput;
        bool changed = ImGui.DragScalar("##drag-number", (int)ImGuiDataType.ImGuiDataType_Double, &number,
            whole ? 1f : .1f, null, null, whole ? "%.0f" : "%.15g", flags) != 0;
        bool active = ImGui.IsItemActive() != 0;
        bool hovered = ImGui.IsItemHovered() != 0;
        int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        if (active || changed)
        {
            if (current is null)
            {
                FinishNumericEdit();
                current = _numericDraft = new NumericDraft(_document, target, field, -1, text, gesture: true);
            }
            _numericDrawn = true;
            current.Dragged |= ImGui.IsMouseDragging(left) != 0 || changed;
            if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0)
            {
                current.Document.CancelEditGroup(current.Group);
                ImGui.ClearActiveID();
                _numericDraft = null;
                return;
            }
            if (changed)
            {
                double candidate = EditorPropertyEdit.ClampNumericDrag(field, number);
                if (EditorPropertyEdit.TryNumber(field, candidate.ToString("R", CultureInfo.InvariantCulture), out JToken validated))
                {
                    CommitProperty(current.Binding, validated, current.Group);
                    current.Version = current.Document.ChangeVersion;
                    _invalidNumber = null;
                }
            }
        }
        bool clicked = current is { Dragged: false } && !active && hovered && ImGui.IsMouseReleased(left) != 0;
        if (clicked || ImGui.IsItemFocused() != 0 &&
            (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Enter, 0) != 0 || ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_KeypadEnter, 0) != 0))
        {
            FinishNumericEdit();
            ImGui.ClearActiveID();
            _numericDraft = new NumericDraft(_document, target, field, -1, text) { FocusText = true };
            _invalidNumber = null;
            _numericDrawn = true;
        }
        else if (!active && current is not null)
            FinishNumericEdit();
        if (hovered && !active)
            ImGui.SetTooltip(L.T(invalid ? "InvalidNumber" : "InlineNumberHelp"));
    }

    private ImVec2 _numericSmokePoint;
    private const string NumericSmokeKey = "__inlineNumberSmoke";
    private const int InlineSmokeLast = 18;
    private const int AngleSmokeStart = 19;
    private const int ColorSmokeStart = 33;
    private const int NumericSmokeLastFrame = 49;
    private ImVec2 _angleSmokeInput, _angleSmokeButton, _colorSmokeSwatch, _colorSmokePicker;
    private int _angleSmokeFloors;

    private static ImVec2 SmokeItemCenter()
    {
        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        return WidgetDraw.V2((min.x + max.x) * .5f, (min.y + max.y) * .5f);
    }

    private void FeedNumericSmokeInput(ImGuiIO* io)
    {
        void Move(ImVec2 point) => ImGuiIO.AddMousePosEvent(io, point.x, point.y);
        void Type(string text)
        {
            foreach (char ch in text) ImGuiIO.AddInputCharacter(io, ch);
        }
        switch (_frames)
        {
            case 5: ImGuiIO.AddFocusEvent(io, 1); break;
            case 6: case 14: case 34: Move(_numericSmokePoint); break;
            case 20: case 27: Move(_angleSmokeInput); break;
            case 24: Move(_angleSmokeButton); break;
            case 40: Move(_colorSmokeSwatch); break;
            case 43: Move(_colorSmokePicker); break;
            case 46: Move(WidgetDraw.V2(260, 245)); break;
            case 7: case 15: case 21: case 25: case 28: case 35: case 41: case 44: case 47:
                ImGuiIO.AddMouseButtonEvent(io, 0, 1); break;
            case 8: case InlineSmokeLast: case 22: case 26: case 29: case 36: case 42: case 45: case 48:
                ImGuiIO.AddMouseButtonEvent(io, 0, 0); break;
            // SetKeyboardFocusHere resolves on the following frame.
            case 11: Type("123.75"); break;
            case 23: Type("112.375"); break;
            case 30: Type("79.625"); break;
            case 37: Type("#38a0e480"); break;
            case 12: case 38: ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiKey_Enter, 1); break;
            case 13: case 39: ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiKey_Enter, 0); break;
            case 31: ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiKey_KeypadEnter, 1); break;
            case 32: ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiKey_KeypadEnter, 0); break;
            case 16: case 17: Move(WidgetDraw.V2(_numericSmokePoint.x + (_frames - 15) * 25, _numericSmokePoint.y)); break;
        }
    }

    private void DrawNumericSmoke()
    {
        if (_frames is >= AngleSmokeStart and < ColorSmokeStart)
        {
            VerifyAngleSmoke();
            return;
        }
        if (_frames is >= ColorSmokeStart and <= NumericSmokeLastFrame)
        {
            DrawColorAndVectorSmoke();
            return;
        }
        if (_frames is < 5 or > InlineSmokeLast) return;
        JObject settings = _document.Settings;
        if (_frames == 5)
            ImGui.SetNextWindowFocus();
        ImGui.SetNextWindowPos(WidgetDraw.V2(240, 160), 0, default);
        ImGui.SetNextWindowSize(WidgetDraw.V2(260, 90));
        if (ImGui.Begin("##number-smoke", (int)ImGuiWindowFlags.ImGuiWindowFlags_NoDocking) != 0)
        {
            DrawNumericInput(settings, new EditorField(NumericSmokeKey, "Float", null, Minimum: 1),
                settings[NumericSmokeKey] ?? new JValue(100d));
            if (_frames == 5) _numericSmokePoint = SmokeItemCenter();
        }
        ImGui.End();
        if (_frames == 13 && Number(settings[NumericSmokeKey]) != 123.75)
            throw new InvalidOperationException("Clicking and typing in the inline numeric field did not commit.");
        if (_frames == InlineSmokeLast)
        {
            if (Number(settings[NumericSmokeKey]) <= 123.75)
                throw new InvalidOperationException("Dragging the inline numeric field did not change its value.");
            _document.Undo();
            if (Number(settings[NumericSmokeKey]) != 123.75)
                throw new InvalidOperationException("Continuous numeric dragging did not undo in one step.");
            _document.Undo();
            if (settings.ContainsKey(NumericSmokeKey))
                throw new InvalidOperationException("The numeric text edit did not undo in one step.");
            Console.WriteLine("Verified inline numeric input, dragging and undo.");
            _showArbitraryAngle = true;
        }
    }

    private void VerifyAngleSmoke()
    {
        if (_frames == AngleSmokeStart) _angleSmokeFloors = _document.LastFloor;
        if (_frames == 23 && (_arbitraryAngle != 112.375 || _document.LastFloor != _angleSmokeFloors))
            throw new InvalidOperationException("Typing an angle did not update the value without inserting a tile.");
        if (_frames == 26 && (_arbitraryAngle != 112.375 || _document.LastFloor != _angleSmokeFloors + 1))
            throw new InvalidOperationException("Adding a tile after typing an angle failed.");
        if (_frames != ColorSmokeStart - 1) return;
        if (_arbitraryAngle != 79.625 || _document.LastFloor != _angleSmokeFloors + 2)
            throw new InvalidOperationException("Pressing Enter in the angle input did not insert exactly one tile.");
        _document.Undo();
        _document.Undo();
        _pendingTiles.Clear();
        SelectFloor(Math.Max(0, _smokeFloor), true);
        _showArbitraryAngle = false;
        Console.WriteLine("Verified live angle input, click and Enter tile insertion.");
    }

    private const string ColorSmokeKey = "__colorSmoke";

    private void DrawColorAndVectorSmoke()
    {
        JObject settings = _document.Settings;
        bool wide = _frames != NumericSmokeLastFrame;
        if (_frames == ColorSmokeStart) ImGui.SetNextWindowFocus();
        ImGui.SetNextWindowPos(WidgetDraw.V2(240, 160), 0, default);
        ImGui.SetNextWindowSize(WidgetDraw.V2(wide ? 260 : 108, 100));
        if (ImGui.Begin("##property-smoke", (int)ImGuiWindowFlags.ImGuiWindowFlags_NoDocking) != 0)
        {
            if (wide)
            {
                float swatch = ImGui.GetFrameHeight();
                ImVec2 start = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().x;
                DrawColorInput(settings, new(ColorSmokeKey, "Color", null), settings[ColorSmokeKey] ?? new JValue("ffffff"));
                _numericSmokePoint = WidgetDraw.V2(start.x + (width - swatch) * .5f, start.y + swatch * .5f);
                _colorSmokeSwatch = WidgetDraw.V2(start.x + width - swatch * .5f, start.y + swatch * .5f);
            }
            if (_frames is ColorSmokeStart or NumericSmokeLastFrame)
            {
                ImVec2 start = ImGui.GetCursorScreenPos();
                DrawVectorInput(settings, new("__vectorSmoke", "Vector2", null), new JArray(1, 2));
                float offsetY = ImGui.GetItemRectMin().y - start.y;
                float expected = wide ? 0 : ImGui.GetFrameHeightWithSpacing();
                if (Math.Abs(offsetY - expected) > 1)
                    throw new InvalidOperationException("Vector inputs did not wrap to the expected rows.");
            }
        }
        ImGui.End();
        if (_frames == 39 && settings.Value<string>(ColorSmokeKey) != "38a0e480")
            throw new InvalidOperationException("Hex color input did not commit.");
        if (_frames != NumericSmokeLastFrame) return;
        if (settings.Value<string>(ColorSmokeKey) is not { } picked || picked == "38a0e480")
            throw new InvalidOperationException("The color picker did not update the input value.");
        _document.Undo();
        if (settings.Value<string>(ColorSmokeKey) != "38a0e480")
            throw new InvalidOperationException("The color picker did not undo in one step.");
        _document.Undo();
        if (settings.ContainsKey(ColorSmokeKey))
            throw new InvalidOperationException("The hex color edit did not undo in one step.");
        Console.WriteLine("Verified vector wrapping and combined hex input and color picker.");
    }

    private void CancelPropertyEdits()
    {
        _numericDraft = null;
        _invalidNumber = null;
        _colorDraft = null;
        _invalidColor = null;
    }

    private void FinishNumericEdit()
    {
        FinishColorEdit();
        if (_numericDraft is not { } draft)
            return;
        _numericDraft = null;
        if (!draft.Changed || draft.Version != draft.Document.ChangeVersion || draft.Gesture)
            return;
        if (!EditorPropertyEdit.TryNumber(draft.Field, draft.Text, out JToken value))
        {
            _invalidNumber = draft;
            return;
        }
        _invalidNumber = null;
        CommitProperty(draft.Binding, EditorPropertyEdit.WithComponent(draft.Binding.Read() ?? draft.Field.Default,
            draft.Field, draft.Component, value));
    }
}
