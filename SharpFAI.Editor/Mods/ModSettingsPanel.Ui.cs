using System.Globalization;
using System.Text;
using System.Text.Json;
using ImGui4NET;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor.Mods;

internal sealed partial class ModSettingsPanel
{
    private sealed class TextDraft(object? value, string source, int capacity)
    {
        public object? Value { get; } = value;
        public byte[] Buffer { get; } = new byte[Math.Max(capacity, Encoding.UTF8.GetByteCount(source) + 1)];
        public bool Edited { get; set; }
    }

    private readonly Dictionary<string, object?> _draft = new(StringComparer.Ordinal);
    private readonly Dictionary<Field, TextDraft> _text = [];
    private string? _error;

    internal void Draw()
    {
        bool open = true;
        foreach (var field in _fields)
        {
            if (field.Hidden) continue;
            if (field.Header is { } header)
            {
                var label = _context.Text(header.Title, header.Title);
                open = ImGui.CollapsingHeader(label + "###mod-group-" + field.Name,
                    (int)ImGuiTreeNodeFlags.ImGuiTreeNodeFlags_DefaultOpen) != 0;
            }
            if (!open) continue;

            bool disabled = field.ReadOnly || !CanEdit(field);
            ImGui.BeginDisabled((byte)(disabled ? 1 : 0));
            try { DrawField(field); }
            finally { ImGui.EndDisabled(); }
            if (field.Tooltip is { } tip &&
                ImGui.IsItemHovered((int)ImGuiHoveredFlags.ImGuiHoveredFlags_AllowWhenDisabled) != 0)
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(_context.Text(tip.Text, tip.Text));
                ImGui.EndTooltip();
            }
        }
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(_path);
        if (_error is { } message) ImGui.TextUnformatted(message);
        ImGui.PopTextWrapPos();

        bool changed = _draft.Count != 0 || _text.Values.Any(value => value.Edited);
        ImGui.BeginDisabled((byte)(changed ? 0 : 1));
        if (ImGui.Button(L.IsChinese ? "应用###apply-settings" : "Apply###apply-settings", default) != 0)
        {
            try
            {
                var values = new Dictionary<string, object?>(_draft, StringComparer.Ordinal);
                foreach (var (field, text) in _text)
                {
                    if (!text.Edited) continue;
                    try { values[field.Name] = ParseText(field, text.Buffer); }
                    catch (Exception error) when (error is FormatException or OverflowException or JsonException or NotSupportedException)
                    {
                        throw new ArgumentException($"{field.Label}: {error.Message}", error);
                    }
                }
                Apply(values);
                ClearDraft();
            }
            catch (Exception error) { _error = error.GetBaseException().Message; }
        }
        ImGui.SameLine();
        if (ImGui.Button(L.IsChinese ? "撤销未应用更改###discard-settings" : "Discard Changes###discard-settings", default) != 0)
            ClearDraft();
        ImGui.EndDisabled();
    }

    private bool CanEdit(Field field)
    {
        if (field.Condition is not { } condition) return true;
        object? value = _draft.TryGetValue(condition.Member, out var draft) ? draft : Get(_members[condition.Member]);
        return value is bool flag && flag != condition.Invert;
    }

    private void DrawField(Field field)
    {
        string name = _context.Text(field.Label, field.Label);
        string label = name + "###mod-setting-" + field.Name;
        object? value = _draft.TryGetValue(field.Name, out var draft) ? draft : Get(field.Member);
        if (value is bool flag)
        {
            if (ImGui.Checkbox(label, ref flag) != 0) _draft[field.Name] = flag;
        }
        else if (field.Options is { } options)
        {
            if (ImGui.BeginCombo(label, value?.ToString() ?? "", 0) == 0) return;
            foreach (string option in options)
                if (ImGui.Selectable(option, (byte)(option == value?.ToString() ? 1 : 0), 0, default) != 0)
                    _draft[field.Name] = Enum.Parse(field.Type, option);
            ImGui.EndCombo();
        }
        else if (value is int integer && field.Range is { } intRange &&
                 intRange.Minimum >= int.MinValue && intRange.Maximum <= int.MaxValue &&
                 Math.Ceiling(intRange.Minimum) < Math.Floor(intRange.Maximum))
        {
            if (ImGui.SliderInt(label, ref integer, (int)Math.Ceiling(intRange.Minimum), (int)Math.Floor(intRange.Maximum), "%d") != 0)
                _draft[field.Name] = integer;
        }
        else if (value is float number && field.Range is { } floatRange &&
                 floatRange.Minimum >= float.MinValue && floatRange.Maximum <= float.MaxValue &&
                 floatRange.Maximum - floatRange.Minimum is > 0 and < float.MaxValue)
        {
            if (ImGui.SliderFloat(label, ref number, (float)floatRange.Minimum, (float)floatRange.Maximum, "%.4g", 0) != 0)
                _draft[field.Name] = number;
        }
        else
        {
            DrawTextField(field, name, label, value);
        }
    }

    private unsafe void DrawTextField(Field field, string name, string label, object? value)
    {
        bool multiline = field.JsonEditor is not null || !field.Number && field.Type != typeof(string);
        if (!_text.TryGetValue(field, out var draft) || !draft.Edited && !Equals(draft.Value, value))
        {
            string source = field.Type == typeof(string) ? (string?)value ?? "" :
                field.Number ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" :
                JsonSerializer.Serialize(value, field.Type, _json);
            draft = new(value, source, multiline ? 65_536 : 4096);
            Encoding.UTF8.GetBytes(source, draft.Buffer);
            _text[field] = draft;
        }
        fixed (byte* buffer = draft.Buffer)
        {
            if (multiline)
            {
                ImGui.TextUnformatted(name);
                int lines = Math.Clamp(field.JsonEditor?.Lines ?? 6, 2, 20);
                draft.Edited |= ImGui.InputTextMultiline("##" + label, (sbyte*)buffer, (nuint)draft.Buffer.Length,
                    WidgetDraw.V2(-1, ImGui.GetTextLineHeightWithSpacing() * lines), 0) != 0;
            }
            else
            {
                draft.Edited |= ImGui.InputText(label, (sbyte*)buffer, (nuint)draft.Buffer.Length) != 0;
            }
        }
    }

    private object? ParseText(Field field, byte[] buffer)
    {
        int end = buffer.AsSpan().IndexOf((byte)0);
        string text = Encoding.UTF8.GetString(buffer.AsSpan(0, end < 0 ? buffer.Length : end));
        if (field.Type == typeof(string)) return text;
        return field.Number ? Convert.ChangeType(text, field.Type, CultureInfo.InvariantCulture)
            : JsonSerializer.Deserialize(text, field.Type, _json);
    }

    private void ClearDraft()
    {
        _draft.Clear();
        _text.Clear();
        _error = null;
    }
}
