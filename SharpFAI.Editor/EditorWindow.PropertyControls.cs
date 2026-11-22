using System.Globalization;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private sealed class ColorDraft(EditorPropertyEdit binding, string text)
    {
        public EditorPropertyEdit Binding { get; } = binding;
        public string Text { get; set; } = text;
        public bool Changed { get; set; }
        public int Version { get; set; } = binding.Document.ChangeVersion;
        public object Group { get; } = new();
    }

    private ColorDraft? _colorDraft;
    private string? _invalidColor;

    private void SetProperty(JObject target, string key, JToken? value) =>
        CommitProperty(EditorPropertyEdit.Capture(_document, target, key), value);

    private void CommitProperty(EditorPropertyEdit binding, JToken? value, object? group = null)
    {
        binding.Set(value, group);
        if (ReferenceEquals(binding.Owner, _particle)) _particleTime = 0;
    }

    private static bool SameBinding(EditorPropertyEdit a, EditorPropertyEdit b) => a.Matches(b);

    private void DrawColorInput(JObject target, EditorField field, JToken? value)
    {
        var binding = EditorPropertyEdit.Capture(_document, target, field.Key);
        ColorDraft? draft = _colorDraft is { } previous && SameBinding(previous.Binding, binding) ? previous : null;
        if (draft is not null && draft.Version != _document.ChangeVersion) draft = _colorDraft = null;
        string text = draft?.Text ?? (value?.Type == JTokenType.String ? value.Value<string>() : null) ?? "";
        string? update = EditText("##hex", L.T("HexColor"), text, true);
        bool active = ImGui.IsItemActive() != 0;
        bool finished = ImGui.IsItemDeactivated() != 0;
        bool enter = active && (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Enter, 0) != 0 ||
            ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_KeypadEnter, 0) != 0);
        bool valid = EditorPropertyEdit.TryColor(update ?? text, out var rgba);
        float* color = stackalloc float[4] { rgba.X, rgba.Y, rgba.Z, rgba.W };
        bool changed = ImGui.ColorEdit4("##color", color, (int)(ImGuiColorEditFlags.ImGuiColorEditFlags_AlphaBar |
            ImGuiColorEditFlags.ImGuiColorEditFlags_NoInputs)) != 0;
        bool popup = ImGui.IsPopupOpen("picker", 0) != 0;
        active |= ImGui.IsItemActive() != 0 || popup;
        finished |= ImGui.IsItemDeactivated() != 0;
        if (active || update is not null || changed)
        {
            if (draft is null)
            {
                FinishNumericEdit();
                draft = _colorDraft = new ColorDraft(binding, text);
                _invalidColor = null;
            }
            _numericDrawn = true;
            if (changed)
            {
                draft.Text = EditorPropertyEdit.ColorHex(new(color[0], color[1], color[2], color[3]));
                CommitProperty(draft.Binding, new JValue(draft.Text), draft.Group);
                draft.Version = draft.Binding.Document.ChangeVersion;
                draft.Changed = true;
            }
            else if (update is not null)
            {
                draft.Text = update;
                draft.Changed = true;
            }
        }
        if (draft is not null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0)
            {
                draft.Binding.Document.CancelEditGroup(draft.Group);
                _colorDraft = null;
                _invalidColor = null;
            }
            else if (enter || !active && (finished || draft.Changed)) FinishColorEdit();
        }
        if (!valid || _invalidColor == string.Join("/", binding.Path))
            WidgetDraw.TextDisabled(L.T("InvalidColor"));
    }

    private void FinishColorEdit()
    {
        if (_colorDraft is not { } draft) return;
        _colorDraft = null;
        if (!draft.Changed || draft.Version != draft.Binding.Document.ChangeVersion) return;
        if (!EditorPropertyEdit.TryColor(draft.Text, out _))
        {
            _invalidColor = string.Join("/", draft.Binding.Path);
            return;
        }
        _invalidColor = null;
        CommitProperty(draft.Binding, new JValue(draft.Text.TrimStart('#')), draft.Group);
    }

    private void DrawAdvancedFilter(JObject target)
    {
        string filter = target.Value<string>("filter") ?? "";
        EditorField[] fields = EditorFilterCatalog.Fields(filter, target.Value<string>("targetType") is not ("Decoration" or "1"));
        if (fields.Length == 0) WidgetDraw.TextDisabled(L.T("UnknownFilter"));
        foreach (var field in fields)
        {
            JObject container = target.ContainsKey(field.Key) ? target :
                target["filterProperties"] is JObject properties && properties.ContainsKey(field.Key) ? properties : target;
            DrawField(container, field);
        }
        if (target["filterProperties"] is JObject nested)
            foreach (var property in nested.Properties().Where(property => fields.All(field => field.Key != property.Name)).ToArray())
                DrawField(nested, EditorCatalog.Infer(property.Name, property.Value));
    }

    private void DrawMinMaxGradient(JObject target, EditorField field, JToken? value)
    {
        JObject? gradient = value as JObject;
        string mode = gradient?.Value<string>("mode") ?? "Color";
        string[] modes = ["Color", "TwoColors", "Gradient", "TwoGradients", "RandomColor"];
        if (ImGui.BeginCombo("##gradient-mode", L.Option(mode)) != 0)
        {
            foreach (string option in modes.Append(mode).Distinct())
                if (ImGui.Selectable(L.Option(option) + "###" + option, (byte)(mode == option ? 1 : 0), 0, default) != 0 && mode != option)
                {
                    FinishNumericEdit();
                    var next = gradient is null ? new JObject { ["color1"] = value?.Type == JTokenType.String ? value.DeepClone() : "ffffff" }
                        : (JObject)gradient.DeepClone();
                    next["mode"] = option;
                    if (option is "Color" or "TwoColors") next["color1"] ??= "ffffff";
                    if (option == "TwoColors") next["color2"] ??= "ffffff";
                    if (option is "Gradient" or "TwoGradients" or "RandomColor") next["gradient1"] ??= DefaultGradient();
                    if (option == "TwoGradients") next["gradient2"] ??= DefaultGradient();
                    SetProperty(target, field.Key, next);
                }
            ImGui.EndCombo();
        }
        if (gradient is null)
        {
            DrawColorInput(target, field, value);
            return;
        }
        if (mode is "Color" or "TwoColors")
        {
            DrawField(gradient, new("color1", "Color", new JValue("ffffff")));
            if (mode == "TwoColors") DrawField(gradient, new("color2", "Color", new JValue("ffffff")));
        }
        else if (mode is "Gradient" or "TwoGradients" or "RandomColor")
        {
            DrawGradient(gradient, "gradient1");
            if (mode == "TwoGradients") DrawGradient(gradient, "gradient2");
        }
        else WidgetDraw.TextDisabled(L.T("UnknownGradient"));
    }

    private static JObject DefaultGradient() => new()
    {
        ["mode"] = "Blend",
        ["colorKeys"] = new JArray(new JObject { ["time"] = 0, ["color"] = "ffffff" }, new JObject { ["time"] = 1, ["color"] = "ffffff" }),
        ["alphaKeys"] = new JArray(new JObject { ["time"] = 0, ["alpha"] = 1 }, new JObject { ["time"] = 1, ["alpha"] = 1 })
    };

    private void DrawGradient(JObject owner, string key)
    {
        ImGui.PushID(key);
        if (owner[key] is not JObject gradient)
        {
            if (ImGui.Button(L.T("InitializeGradient"), default) != 0) SetProperty(owner, key, DefaultGradient());
            ImGui.PopID();
            return;
        }
        DrawField(gradient, new("mode", "Enum:GradientMode", new JValue("Blend"), Options: ["Blend", "Fixed"]));
        foreach (string keys in new[] { "colorKeys", "alphaKeys" })
        {
            bool alpha = keys == "alphaKeys";
            ImGui.PushID(keys);
            ImGui.TextUnformatted(L.T(alpha ? "AlphaKeys" : "ColorKeys"));
            var array = gradient[keys] as JArray;
            foreach (var (item, index) in (array ?? []).Select((item, index) => (item, index)).ToArray())
            {
                ImGui.PushID(index);
                if (item is JObject point)
                {
                    DrawField(point, new("time", "Float", new JValue(0), Minimum: 0, Maximum: 1));
                    DrawField(point, alpha ? new("alpha", "Float", new JValue(1), Minimum: 0, Maximum: 1)
                        : new("color", "Color", new JValue("ffffff")));
                }
                else WidgetDraw.TextDisabled(L.T("UnknownGradient"));
                ImGui.BeginDisabled((byte)((array?.Count ?? 0) <= 2 ? 1 : 0));
                if (ImGui.SmallButton(L.T("RemoveKey")) != 0)
                {
                    FinishNumericEdit();
                    var binding = EditorPropertyEdit.Capture(_document, gradient, keys);
                    if (binding.Read() is JArray current)
                    {
                        var next = (JArray)current.DeepClone();
                        next.RemoveAt(index);
                        CommitProperty(binding, next);
                    }
                }
                ImGui.EndDisabled();
                ImGui.PopID();
            }
            ImGui.BeginDisabled((byte)((array?.Count ?? 0) >= 8 ? 1 : 0));
            if (ImGui.SmallButton(L.T("AddKey")) != 0)
            {
                FinishNumericEdit();
                var binding = EditorPropertyEdit.Capture(_document, gradient, keys);
                var next = binding.Read() is JArray current ? (JArray)current.DeepClone() : new JArray();
                next.Add(alpha ? new JObject { ["time"] = 0.5, ["alpha"] = 1 } : new JObject { ["time"] = 0.5, ["color"] = "ffffff" });
                CommitProperty(binding, next);
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.PopID();
    }
}
