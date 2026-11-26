using System.Text;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private void DrawModEvents()
    {
        var entries = _modUi?.Host.Entries<CustomEventCompiler>(UiHost.Editor) ?? [];
        JObject[] events = _document.ModEvents?.OfType<JObject>()
            .Where(item => item["anchor"]?["floor"]?.Value<int?>() == _floor).ToArray() ?? [];
        if (entries.Count == 0 && events.Length == 0) return;
        if (ImGui.CollapsingHeader(L.IsChinese ? "Mod 事件###mod-events" : "Mod events###mod-events", 0) == 0)
            return;

        bool editable = _playbackView == PlaybackView.Edit;
        ImGui.BeginDisabled((byte)(editable ? 0 : 1));
        if (ImGui.BeginCombo("##mod-event-type", L.IsChinese ? "添加 Mod 事件" : "Add Mod event", 0) != 0)
        {
            foreach (var entry in entries)
            {
                using var lease = entry.Acquire();
                if (lease is null || entry.Value.Schema is not { } schema) continue;
                if (ImGui.Selectable(entry.Text(schema.TitleKey) + "###" + entry.Descriptor.Id, 0, 0, default) != 0)
                    _document.AddModEvent(entry.Descriptor.Id, schema, _floor);
            }
            ImGui.EndCombo();
        }
        foreach (var item in events)
        {
            string? type = item.Value<string>("type");
            string stableId = item.Value<string>("id") ?? _document.ModEvents!.IndexOf(item).ToString();
            var entry = entries.FirstOrDefault(candidate => candidate.Descriptor.Id == type);
            using var lease = entry?.Acquire();
            ImGui.PushID(stableId);
            var schema = lease is null ? null : entry?.Value.Schema;
            string title = schema is not null ? entry!.Text(schema.TitleKey) : type ?? "Mod event";
            if (ImGui.TreeNodeEx(title + "###mod-event-instance", 0) != 0)
            {
                if (schema is { } known && item["payload"] is JObject payload &&
                    (item.Value<int?>("schemaVersion") ?? 1) == known.Version)
                {
                    foreach (var field in known.Fields) DrawModEventField(entry!, field, payload);
                }
                else
                    ImGui.TextUnformatted(L.IsChinese ? "字段暂不可编辑，数据会保留。" : "Fields are unavailable. Saved data is preserved.");
                if (editable && ImGui.SmallButton(L.IsChinese ? "删除事件" : "Remove event") != 0)
                    _document.RemoveModEvent(item);
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        ImGui.EndDisabled();
    }

    private void DrawModEventField(ModRegistration<CustomEventCompiler> entry, CustomEventField field, JObject payload)
    {
        string label = entry.Text(field.TitleKey) + "###" + field.Key;
        JToken value = payload[field.Key] ?? JToken.Parse(field.Default.GetRawText());
        switch (field.Kind)
        {
            case CustomFieldKind.Number when value.Type is JTokenType.Integer or JTokenType.Float:
                float number = value.Value<float>();
                if (ImGui.DragFloat(label, ref number, .01f, field.Minimum, field.Maximum, "%.3f",
                        (int)ImGuiSliderFlags.ImGuiSliderFlags_AlwaysClamp) != 0 && float.IsFinite(number))
                    _document.Set(payload, field.Key, new JValue(Math.Clamp(number, field.Minimum, field.Maximum)));
                break;
            case CustomFieldKind.Toggle when value.Type == JTokenType.Boolean:
                bool enabled = value.Value<bool>();
                if (ImGui.Checkbox(label, ref enabled) != 0) _document.Set(payload, field.Key, new JValue(enabled));
                break;
            case CustomFieldKind.Choice when field.Choices is { } options:
                if (ImGui.BeginCombo(label, value.ToString(), 0) == 0) break;
                foreach (string option in options)
                    if (ImGui.Selectable(option, (byte)(option == value.ToString() ? 1 : 0), 0, default) != 0)
                        _document.Set(payload, field.Key, new JValue(option));
                ImGui.EndCombo();
                break;
            case CustomFieldKind.Text when value.Type == JTokenType.String:
                string text = value.ToString();
                int size = Encoding.UTF8.GetByteCount(text);
                if (size >= 4096) { ImGui.TextUnformatted(text); break; }
                Span<byte> buffer = stackalloc byte[4096];
                buffer.Clear();
                Encoding.UTF8.GetBytes(text, buffer);
                fixed (byte* target = buffer)
                    if (ImGui.InputText(label, (sbyte*)target, (nuint)buffer.Length) != 0)
                        _document.Set(payload, field.Key, new JValue(Encoding.UTF8.GetString(buffer[..buffer.IndexOf((byte)0)])));
                break;
            default:
                ImGui.TextUnformatted(value.ToString());
                break;
        }
    }
}
