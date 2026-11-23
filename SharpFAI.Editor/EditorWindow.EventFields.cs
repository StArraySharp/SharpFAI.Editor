using System.Text;
using ImGui4NET;
using NativeFileDialog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private EditorPropertyEdit? _multilineBinding;
    private int _multilineVersion;
    private string _multilineText = "";
    private string[] _tagSuggestions = [];

    private static JValue? DrawPropertyChoice(string id, EditorField field, JToken? value, string[] options)
    {
        string? current = value?.Type == JTokenType.String ? value.Value<string>() : null;
        string display = current is not null && options.Contains(current, StringComparer.Ordinal)
            ? L.Option(current, field) : current ?? value?.ToString(Formatting.None) ?? "";
        if (ImGui.BeginCombo(id, display) == 0) return null;
        JValue? change = null;
        foreach (string option in options)
        {
            bool selected = option == current;
            if (ImGui.Selectable(L.Option(option, field) + "###" + option, (byte)(selected ? 1 : 0), 0, default) != 0)
                change = InspectorFields.SelectChoice(value, option);
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
        return change;
    }

    private JToken? DrawSoundChoice(EditorField field, JToken? value, string[] options)
    {
        JValue? choice = options.Length > 0 ? DrawPropertyChoice("##sound", field, value, options) : null;
        return DrawAssetField(field, choice ?? value, "Audio") ?? choice;
    }

    private JToken? DrawAssetField(EditorField field, JToken? value, string assetType)
    {
        const string browse = "...##browse";
        bool beside = ReserveFieldButton(browse);
        JToken? updated = null;
        if (value?.Type is null or JTokenType.Null or JTokenType.String)
        {
            if (EditText("##asset", field.Unit ?? "", value?.Value<string>() ?? "", false) is { } text)
                updated = new JValue(text);
        }
        else updated = DrawRawField(value);
        if (beside) ImGui.SameLine();
        if (ImGui.Button(browse, default) != 0)
        {
            try
            {
                List<string> extensions = assetType switch
                {
                    "Image" => ["png", "jpg", "jpeg", "bmp", "gif", "tga", "webp"],
                    "Audio" => ["ogg", "wav", "mp3", "flac", "aif", "aiff"],
                    "Video" => ["mp4", "mov", "webm", "m4v", "avi", "ogv"],
                    _ => []
                };
                string? basePath = Path.GetDirectoryName(_document.Path);
                string title = L.T("ChooseAsset");
                var filter = new OpenFileFilter { IncludeAllFiles = true, Filter = [] };
                if (extensions.Count > 0) filter.Filter.Add(title, extensions);
                string[]? paths = NativeFileDialog.NativeFileDialog.GetFileDialog()?.OpenFile(title, basePath ?? "", filter);
                if (paths is { Length: > 0 })
                    updated = new JValue(field.Key == "decorationImage" ? _document.ImportDecorationImage(paths[0]) :
                        basePath is not null ? Path.GetRelativePath(basePath, paths[0]) : paths[0]);
            }
            catch (Exception error) { ShowError(error); }
        }
        WidgetDraw.ItemTooltip(L.T("PropertyBrowse"));
        return updated;
    }

    private bool ReserveFieldButton(string button)
    {
        float width = ButtonWidth(button.Split("##", 2)[0]) + ImGui.GetStyle()->ItemSpacing.x;
        bool beside = ImGui.GetContentRegionAvail().x > width + 70 * _uiScale;
        ImGui.SetNextItemWidth(beside ? -width : -1);
        return beside;
    }

    private JToken? DrawRawField(JToken? value)
    {
        if (EditText("##json", "JSON", value?.ToString(Formatting.None) ?? "null", false) is not { } json) return null;
        try { return JToken.Parse(json); }
        catch (JsonReaderException error) { ShowError(error); return null; }
    }

    private JToken? DrawMultilineField(JObject target, EditorField field, JToken? value)
    {
        const string popupId = "###property-multiline";
        string title = L.T("PropertyTextEdit");
        string text = value?.Value<string>() ?? "";
        EditorPropertyEdit binding = EditorPropertyEdit.Capture(_document, target, field.Key);
        if (ImGui.Button(title + "##edit", default) != 0)
        {
            FinishNumericEdit();
            _multilineBinding = binding;
            _multilineVersion = _document.ChangeVersion;
            _multilineText = binding.Read()?.Value<string>() ?? text;
            ImGui.OpenPopup(title + popupId);
        }
        if (text.Length != 0)
        {
            string line = text.Split(['\r', '\n'], 2)[0];
            ImGui.PushTextWrapPos();
            WidgetDraw.TextDisabled(line.Length > 96 ? line[..96] + "…" : line.Length < text.Length ? line + "…" : line);
            ImGui.PopTextWrapPos();
        }
        if (ImGui.BeginPopupModal(title + popupId, (int)ImGuiWindowFlags.ImGuiWindowFlags_AlwaysAutoResize) == 0)
            return null;

        JToken? updated = null;
        if (_multilineBinding is null || !SameBinding(_multilineBinding, binding) || _multilineVersion != _document.ChangeVersion)
        {
            _multilineBinding = null;
            ImGui.CloseCurrentPopup();
        }
        else
        {
            ImGuiIO* io = ImGui.GetIO();
            float width = Math.Clamp(io->DisplaySize.x - 64, 100, 440 * _uiScale);
            float height = Math.Clamp(io->DisplaySize.y - 140, ImGui.GetFrameHeight(), 154 * _uiScale);
            if (ImGui.IsWindowAppearing() != 0) ImGui.SetKeyboardFocusHere();
            _multilineText = EditMultiline(_multilineText, width, height) ?? _multilineText;
            if (ImGui.Button(L.T("PropertyApply"), default) != 0)
            {
                if (_multilineText != text) updated = new JValue(_multilineText);
                _multilineBinding = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(L.T("Cancel"), default) != 0 || ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0)
            {
                _multilineBinding = null;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
        return updated;
    }

    private static string? EditMultiline(string value, float width, float height)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = count < 1024 ? stackalloc byte[4096] : new byte[count + 4096];
        int length = Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
        buffer[length] = 0;
        fixed (byte* pointer = buffer)
            return ImGui.InputTextMultiline("##draft", (sbyte*)pointer, (nuint)buffer.Length, WidgetDraw.V2(width, height)) != 0
                ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)pointer)
                : null;
    }

    private JToken? DrawTileField(JObject target, EditorField field, JToken? value)
    {
        if (InspectorFields.ReadTile(value) is not { } tile) return DrawRawField(value);
        var binding = EditorPropertyEdit.Capture(_document, target, field.Key);
        JToken? change = null;
        if (value?.Type == JTokenType.String)
        {
            if (EditText("##legacy-offset", "", tile[0]?.ToString() ?? "0", false) is { } edit &&
                EditorPropertyEdit.TryNumber(field, edit, out JToken offset) && !JToken.DeepEquals(offset, tile[0]))
                change = InspectorFields.WithTilePart(tile, 0, offset);
        }
        else DrawNumericInput(target, field, tile.ElementAtOrDefault(0), 0);

        ImGui.SetNextItemWidth(-1);
        var anchor = tile.ElementAtOrDefault(1) ?? new JValue("ThisTile");
        if (DrawPropertyChoice("##anchor", field, anchor, EditorCatalog.TileReferenceOptions) is { } choice)
        {
            FinishNumericEdit();
            var latest = change as JArray ?? InspectorFields.ReadTile(binding.Read() ?? value) ?? tile;
            change = InspectorFields.WithTilePart(latest, 1, choice);
        }
        var preview = change as JArray ?? InspectorFields.ReadTile(binding.Read() ?? value) ?? tile;
        if (InspectorFields.ResolveTile(preview, EditorDocument.Floor(binding.Owner), _document.LastFloor) is { } floor)
            WidgetDraw.TextDisabled(L.F("TileStatus", floor, _document.LastFloor));
        return change;
    }

    private JToken? DrawTagField(EditorField field, JToken? value, string source)
    {
        const string button = "+##tag-list";
        string current = value?.Value<string>() ?? "";
        bool beside = ReserveFieldButton(button);
        string? updated = EditText("##value", field.Unit ?? "", current, false);
        if (beside) ImGui.SameLine();
        if (ImGui.Button(button, default) != 0)
        {
            IEnumerable<JObject> items = source == "tag" ? _document.Decorations.OfType<JObject>() :
                Enumerable.Range(0, _document.LastFloor + 1).SelectMany(_document.Events);
            _tagSuggestions = items.SelectMany(item => item[source]?.Type == JTokenType.String
                    ? item[source]!.Value<string>()!.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (_tagSuggestions.Length > 0) ImGui.OpenPopup("##tags");
        }
        WidgetDraw.ItemTooltip(L.T("PropertyTagSuggestions"));
        if (ImGui.BeginPopup("##tags") != 0)
        {
            float height = Math.Min(_tagSuggestions.Length, 10) * ImGui.GetFrameHeightWithSpacing();
            if (ImGui.BeginChild("##tag-options", WidgetDraw.V2(186 * _uiScale, height)) != 0)
                for (int i = 0; i < _tagSuggestions.Length; i++)
                    if (ImGui.Selectable(_tagSuggestions[i] + "###tag-" + i, 0, 0, default) != 0)
                    {
                        string next = InspectorFields.MergeTag(updated ?? current, _tagSuggestions[i]);
                        if (next != current) updated = next;
                        ImGui.CloseCurrentPopup();
                    }
            ImGui.EndChild();
            ImGui.EndPopup();
        }
        return updated is null ? null : new JValue(updated);
    }
}
