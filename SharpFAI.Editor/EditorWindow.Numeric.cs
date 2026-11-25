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

    private static string NumberUnit(string? unit) => unit switch
    {
        "bpm" => "BPM",
        "seconds" => "s",
        "pixels" => "px",
        "X" => "×",
        "beats" => L.T("UnitBeats"),
        "tiles" or "tilesFrom" => L.T("UnitTiles"),
        "loops" => L.T("UnitLoops"),
        _ => unit ?? "",
    };

    private static float NumberUnitWidth(string unit) =>
        unit.Length == 0 ? 0 : ImGui.CalcTextSize(unit).x + ImGui.GetStyle()->ItemInnerSpacing.x;

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
        bool sideBySide = half >= numberOffset + 52 * _uiScale + NumberUnitWidth(NumberUnit(field.Unit));
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
        string unit = NumberUnit(field.Unit);
        float unitWidth = NumberUnitWidth(unit);
        float buttonWidth = ImGui.GetFrameHeight();
        float stepWidth = 2 * (buttonWidth + ImGui.GetStyle()->ItemSpacing.x);
        step &= available >= stepWidth + unitWidth + 52 * _uiScale;
        ImGui.SetNextItemWidth(Math.Max(1, available - unitWidth - (step ? stepWidth : 0)));
        if (component < 0 && !field.AllowEmpty && current is not { Gesture: false })
            DrawInlineNumberDrag(target, field, value, content, current, invalid is not null);
        else
            DrawNumberText(target, field, component, content, current);
        if (unitWidth > 0)
        {
            ImGui.SameLine(0, ImGui.GetStyle()->ItemInnerSpacing.x);
            ImGui.AlignTextToFramePadding();
            WidgetDraw.TextDisabled(unit);
        }
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
        string? update = EditText("##number", "", content, live: true,
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
