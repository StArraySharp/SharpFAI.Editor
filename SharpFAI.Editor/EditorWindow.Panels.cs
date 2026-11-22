using System.Globalization;
using System.Text;
using ImGui4NET;
using NativeFileDialog;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly HashSet<JObject> _selectedDecorations = new(ReferenceEqualityComparer.Instance);
    private bool _hideAllDecorations;
    private bool _lockAllDecorations;
    private string _decorationFilter = "";
    private string _eventSearch = "";

    private void DrawSettings()
    {
        if (!_showSettings)
            return;
        if (BeginPanel(SettingsWindow, ref _showSettings) && ImGui.BeginTabBar("##settings-tabs",
                (int)ImGuiTabBarFlags.ImGuiTabBarFlags_FittingPolicyScroll) != 0)
        {
            foreach (string tab in _catalog.SettingsTabs)
            {
                if (ImGui.BeginTabItem(EditorCatalog.Name(tab)) == 0)
                    continue;
                if (ImGui.BeginChild("##fields", default) != 0)
                    foreach (EditorField field in _catalog.SettingsFor(tab))
                        if (!field.Hidden && IsDataField(field))
                            DrawField(_document.Settings, field);
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawDecorations()
    {
        if (!_showDecorations)
            return;
        if (_focusDecorations)
        {
            ImGui.SetNextWindowFocus();
            _focusDecorations = false;
        }
        if (BeginPanel(DecorationsWindow, ref _showDecorations))
        {
            ImGui.SetNextItemWidth(-1);
            if (EditText("##decoration-search", L.T("SearchDecorations"), _decorationFilter, true) is { } filter)
                _decorationFilter = filter;
            if (ImGui.Button(L.T("AddDecoration"), default) != 0)
                ImGui.OpenPopup("##add-decoration");
            if (ImGui.BeginPopup("##add-decoration") != 0)
            {
                foreach (string type in EditorCatalog.DecorationTypes)
                    if (_catalog.Find(type) is not null && ImGui.MenuItem(EditorCatalog.Name(type)) != 0)
                        AddEvent(type);
                ImGui.EndPopup();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(_hideAllDecorations ? L.T("ShowAll") : L.T("HideAll")) != 0)
                _hideAllDecorations = !_hideAllDecorations;
            ImGui.SameLine();
            if (ImGui.SmallButton(_lockAllDecorations ? L.T("UnlockAll") : L.T("LockAll")) != 0)
                _lockAllDecorations = !_lockAllDecorations;
            ImGui.Separator();

            string[] terms = _decorationFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ImGui.BeginChild("##decoration-list", default) != 0)
            {
                var decorations = _document.Decorations;
                for (int i = 0; i < decorations.Count; i++)
                {
                    if (decorations[i] is not JObject item || !MatchesDecoration(item, terms))
                        continue;
                    ImGui.PushID(i);
                    float width = Math.Max(25, ImGui.GetContentRegionAvail().x - 100);
                    if (ImGui.Selectable(DecorationName(item) + "###item", (byte)(_selectedDecorations.Contains(item) ? 1 : 0),
                            0, WidgetDraw.V2(width, 0)) != 0)
                    {
                        SelectDecoration(item, ImGui.GetIO()->KeyCtrl != 0);
                        CenterOnDecoration(item);
                    }
                    if (ImGui.BeginDragDropSource() != 0)
                    {
                        int original = i;
                        ImGui.SetDragDropPayload("DECORATION", &original, sizeof(int));
                        ImGui.TextUnformatted(DecorationName(item));
                        ImGui.EndDragDropSource();
                    }
                    if (ImGui.BeginDragDropTarget() != 0)
                    {
                        ImGuiPayload* payload = ImGui.AcceptDragDropPayload("DECORATION");
                        if (payload != null && payload->DataSize == sizeof(int))
                        {
                            int from = *(int*)payload->Data;
                            if (from >= 0 && from < decorations.Count && decorations[from] is JObject moved)
                                _document.MoveDecoration(moved, item);
                        }
                        ImGui.EndDragDropTarget();
                    }
                    ImGui.SameLine();
                    bool hidden = item.Value<bool?>("visible") == false;
                    if (ImGui.SmallButton(hidden ? L.T("Show") : L.T("Hide")) != 0)
                        _document.Set(item, "visible", hidden ? null : new JValue(false));
                    ImGui.SameLine();
                    bool locked = item.Value<bool?>("locked") == true;
                    if (ImGui.SmallButton(locked ? L.T("Unlock") : L.T("Lock")) != 0)
                        _document.Set(item, "locked", locked ? null : new JValue(true));
                    ImGui.PopID();
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static void ToggleSet(HashSet<JObject> set, JObject item)
    {
        if (!set.Remove(item))
            set.Add(item);
    }

    private bool DecorationHidden(JObject item) => _hideAllDecorations || item.Value<bool?>("visible") == false;
    private bool DecorationLocked(JObject item) => _lockAllDecorations || item.Value<bool?>("locked") == true;

    private bool IsSelectedParticle => _inspectDecoration && _decoration is { Parent: not null } &&
                                       EditorDocument.Type(_decoration) == "AddParticle";

    private void SelectDecoration(JObject item, bool toggle = false)
    {
        if (!toggle)
            _selectedDecorations.Clear();
        ToggleSet(_selectedDecorations, item);
        _decoration = _selectedDecorations.Contains(item) ? item : _selectedDecorations.LastOrDefault();
        _inspectDecoration = _decoration is not null;
        _showInspector = true;
    }

    private static bool MatchesDecoration(JObject item, string[] terms)
    {
        string name = DecorationName(item);
        string type = EditorDocument.Type(item);
        string tag = item.Value<string>("tag") ?? "";
        foreach (string term in terms)
        {
            if (term.StartsWith("floor:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(term.AsSpan(6), out int floor) || floor != EditorDocument.Floor(item)) return false;
            }
            else if (term.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                if (!type.Contains(term[5..], StringComparison.OrdinalIgnoreCase) &&
                    !EditorCatalog.Name(type).Contains(term[5..], StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (term.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                if (!tag.Contains(term[4..], StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                     !tag.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                     !EditorCatalog.Name(type).Contains(term, StringComparison.OrdinalIgnoreCase) &&
                     !type.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                     (!int.TryParse(term, out int floor) || floor != EditorDocument.Floor(item)))
                return false;
        }
        return true;
    }

    private static string DecorationName(JObject item)
    {
        string type = EditorDocument.Type(item);
        string? tag = item.Value<string>("tag");
        string? label = type == "AddText" ? item.Value<string>("decText") : item.Value<string>("decorationImage");
        string name = !string.IsNullOrWhiteSpace(tag) ? tag : !string.IsNullOrWhiteSpace(label) ? label : EditorCatalog.Name(type);
        int newline = name.IndexOfAny(['\r', '\n']);
        return newline < 0 ? name : name[..newline];
    }

    private void DrawInspector()
    {
        if (!_showInspector)
            return;
        if (BeginPanel(InspectorWindow, ref _showInspector))
        {
            if (_inspectDecoration && _decoration is { Parent: not null } decoration)
            {
                ImGui.TextUnformatted(DecorationName(decoration));
                ImGui.SameLine();
                WidgetDraw.TextDisabled(EditorCatalog.Name(EditorDocument.Type(decoration)));
                if (ImGui.Button(L.T("LocateDecoration"), default) != 0)
                    CenterOnDecoration(decoration);
                if (IsSelectedParticle)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(L.T("EditParticle"), default) != 0)
                        OpenParticle();
                }
                ImGui.Separator();
                DrawProperties(decoration, true);
            }
            else
            {
                if (ImGui.SmallButton("<##previous-floor") != 0)
                    SelectFloor(_floor - 1, true);
                ImGui.SameLine();
                ImGui.TextUnformatted(_rangeAnchor is null ? L.F("TileStatus", _floor, _document.LastFloor) :
                    L.F("TileRangeStatus", FloorRange.First, FloorRange.Last, _document.LastFloor));
                ImGui.SameLine();
                if (ImGui.SmallButton(">##next-floor") != 0)
                    SelectFloor(_floor + 1, true);
                ImGui.Separator();
                if (_hasFloorSelection)
                    DrawFloorEvents();
            }
        }
        ImGui.End();
    }

    private void DrawFloorEvents()
    {
        IReadOnlyList<JObject> events = _document.Events(_floor);
        string[] types = events.Select(EditorDocument.Type).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length == 0)
        {
            WidgetDraw.TextDisabled(L.T("AddEventHint"));
            return;
        }
        if (_eventType is null || !types.Contains(_eventType))
        {
            _eventType = types[0];
            _eventPage = 0;
        }
        foreach (string type in types)
        {
            ImGui.PushID(type);
            int count = events.Count(item => EditorDocument.Type(item) == type);
            bool selected = _eventType == type;
            bool paginate = count > 1 && selected;
            float rowWidth = ImGui.GetContentRegionAvail().x;
            float width = paginate ? Math.Max(36, rowWidth - 98) : rowWidth;
            if (ImGui.Selectable(EditorCatalog.Name(type), (byte)(selected ? 1 : 0), 0, WidgetDraw.V2(width, 0)) != 0 && !selected)
            {
                _eventType = type;
                _eventPage = 0;
            }
            if (paginate)
            {
                _eventPage = Math.Clamp(_eventPage, 0, count - 1);
                ImGui.SameLine();
                ImGui.BeginDisabled((byte)(_eventPage == 0 ? 1 : 0));
                if (ImGui.SmallButton("<") != 0) _eventPage--;
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextUnformatted($"{_eventPage + 1}/{count}");
                ImGui.SameLine();
                ImGui.BeginDisabled((byte)(_eventPage == count - 1 ? 1 : 0));
                if (ImGui.SmallButton(">") != 0) _eventPage++;
                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }

        JObject[] pages = events.Where(item => EditorDocument.Type(item) == _eventType).ToArray();
        _eventPage = Math.Clamp(_eventPage, 0, pages.Length - 1);
        JObject item = pages[_eventPage];
        ImGui.Separator();
        bool enabled = Enabled(item);
        if (Toggle(L.T("EventEnabled"), ref enabled))
            _document.Set(item, "active", enabled ? null : new JValue(false));
        ImGui.SameLine();
        if (ImGui.SmallButton(L.T("DeleteEvent")) != 0)
        {
            _document.Remove(item);
            return;
        }
        if (ImGui.BeginChild("##event-fields", default) != 0)
            DrawProperties(item, false);
        ImGui.EndChild();
    }

    private void ChangeEventPage(int direction, bool jump)
    {
        if (_inspectDecoration)
            return;
        int count = _document.Events(_floor).Count(item => EditorDocument.Type(item) == _eventType);
        if (count != 0)
            _eventPage = jump ? (direction < 0 ? 0 : count - 1) : Math.Clamp(_eventPage + direction, 0, count - 1);
    }

    private void CycleEvent(int direction)
    {
        if (_inspectDecoration)
            return;
        string[] types = _document.Events(_floor).Select(EditorDocument.Type).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length == 0)
            return;
        int i = Math.Max(0, Array.IndexOf(types, _eventType));
        _eventType = types[(i + direction + types.Length) % types.Length];
        _eventPage = 0;
    }

    private void DrawPalette()
    {
        if (!_showPalette)
            return;
        if (BeginPanel(PaletteWindow, ref _showPalette))
        {
            if (ImGui.Button("|<##restart", default) != 0) SelectFloor(0, true);
            WidgetDraw.ItemTooltip(L.T("GoToStart"));
            string preview = L.T(_playbackView == PlaybackView.Preview ? "ReturnToEdit" : "ChartPreview");
            ContinueToolbar(ButtonWidth(preview));
            if (ImGui.Button(preview, default) != 0)
                SwitchPlaybackView(_playbackView == PlaybackView.Preview ? PlaybackView.Edit : PlaybackView.Preview);
            string play = L.T(_playbackView == PlaybackView.Auto ? "Pause" : "Play");
            ContinueToolbar(ButtonWidth(play));
            if (ImGui.Button(play, default) != 0)
            {
                if (_playbackView == PlaybackView.Auto) PauseToPreview();
                else SwitchPlaybackView(PlaybackView.Auto, HeldPlaybackRate);
            }
            WidgetDraw.ItemTooltip(L.T("PlayTooltip"));
            ContinueToolbar(ButtonWidth(L.T("Manual")));
            if (ImGui.Button(L.T("Manual"), default) != 0) SwitchPlaybackView(PlaybackView.Manual, HeldPlaybackRate);
            WidgetDraw.ItemTooltip(L.T("ManualTooltip"));
            ContinueToolbar(76 * _uiScale);
            DrawPlaybackSpeed();
            ImGui.Separator();
            for (int i = 0; i < _catalog.Categories.Length; i++)
            {
                string label = EditorCatalog.Name(_catalog.Categories[i].Key);
                if (i > 0)
                    ContinueToolbar(ImGui.CalcTextSize(label).x + ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x);
                if (ImGui.RadioButton(label, (byte)(i == _category ? 1 : 0)) != 0)
                {
                    _category = i;
                    _eventSearch = "";
                }
            }
            ContinueToolbar(105 * _uiScale);
            ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().x, 1, 210 * _uiScale));
            if (EditText("##event-search", L.T("SearchEvents"), _eventSearch, true) is { } search)
                _eventSearch = search;

            if (ImGui.BeginChild("##event-buttons", default, 0,
                    (int)ImGuiWindowFlags.ImGuiWindowFlags_HorizontalScrollbar) != 0)
            {
                IEnumerable<string> names = PaletteEvents();
                bool first = true;
                int shortcut = 0;
                foreach (string name in names)
                {
                    if (!first) ImGui.SameLine();
                    first = false;
                    EditorEvent? definition = _catalog.Find(name);
                    bool disabled = definition is null || !CanAddEvent(definition);
                    ImGui.BeginDisabled((byte)(disabled ? 1 : 0));
                    if (ImGui.Button(EditorCatalog.Name(name) + "###" + name, WidgetDraw.V2(0, 36)) != 0)
                        AddEvent(name);
                    // Keep number-key positions stable when an event already occupies its slot.
                    if (_hasFloorSelection && (_floor != 0 || definition?.AllowFirstFloor != false) && shortcut < 10)
                    {
                        shortcut++;
                        if (!disabled)
                            WidgetDraw.ItemTooltip((shortcut % 10).ToString());
                    }
                    ImGui.EndDisabled();
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private IEnumerable<string> PaletteEvents() => _eventSearch.Length == 0
        ? _catalog.Categories[_category].Events
        : _catalog.Categories.SelectMany(category => category.Events).Distinct().Where(name =>
            name.Contains(_eventSearch, StringComparison.OrdinalIgnoreCase) ||
            EditorCatalog.Name(name).Contains(_eventSearch, StringComparison.OrdinalIgnoreCase));

    private bool CanAddEvent(EditorEvent definition) =>
        (_floor != 0 || definition.AllowFirstFloor) &&
        (definition.Decoration || _hasFloorSelection && _document.CanAddEvent(_floor, definition.Type));

    private void AddEvent(string type)
    {
        if (_catalog.Find(type) is not { } definition || !CanAddEvent(definition))
            return;
        JObject item = _catalog.Create(type, _floor);
        if (!_document.Add(item, definition.Decoration))
            return;
        _showInspector = true;
        if (definition.Decoration)
        {
            SelectDecoration(item);
            CenterOnDecoration(item);
        }
        else
        {
            _inspectDecoration = false;
            _eventType = type;
            _eventPage = _document.Events(_floor).Count(value => EditorDocument.Type(value) == type) - 1;
        }
    }

    private void DrawProperties(JObject target, bool decoration)
    {
        string type = EditorDocument.Type(target);
        EditorField[] fields = _catalog.Find(type)?.Fields ?? [];
        ImGui.PushID(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target));
        if (decoration && fields.All(field => field.Key != "floor"))
            DrawField(target, new("floor", "Int"));
        foreach (EditorField field in fields)
        {
            if (!IsDataField(field) || field.Hidden)
                continue;
            if (type == "AddParticle" && EditorCatalog.ParticleTabs.Any(tab => tab.Keys.Contains(field.Key)))
                continue;
            DrawField(target, field);
        }

        if (type == "SetFilterAdvanced" && fields.All(field => field.Key != "filterProperties"))
            DrawAdvancedFilter(target);
        if (ImGui.CollapsingHeader(L.T("OtherFields")) != 0)
        {
            foreach (JProperty property in target.Properties().ToArray())
            {
                if (property.Name is "floor" or "eventType" or "active" or "locked" or "visible" or "disabled" ||
                    fields.Any(field => field.Key == property.Name) || type == "SetFilterAdvanced" &&
                    (property.Name == "filterProperties" || EditorFilterCatalog.Fields(target.Value<string>("filter") ?? "").Any(field => field.Key == property.Name)))
                    continue;
                DrawField(target, EditorCatalog.Infer(property.Name, property.Value));
            }
        }
        ImGui.PopID();
    }

    private void DrawField(JObject target, EditorField field)
    {
        string key = field.Key;
        bool dependencyDisabled = false;
        if (field.DisableIf is { Count: >= 2 } condition)
        {
            string? dependency = condition[0]?.Value<string>();
            string? expected = condition[1]?.ToString();
            JToken? dependencyValue = dependency is null ? null : target[dependency];
            dependencyDisabled = string.Equals(dependencyValue?.ToString(), expected, StringComparison.OrdinalIgnoreCase) ||
                dependencyValue?.Type == JTokenType.Boolean && expected is "Enabled" or "Disabled" &&
                dependencyValue.Value<bool>() == (expected == "Enabled");
        }
        if (EditorDocument.Type(target) == "SetSpeed" &&
            (key == "beatsPerMinute" && target.Value<string>("speedType") == "Multiplier" ||
             key == "bpmMultiplier" && target.Value<string>("speedType") != "Multiplier"))
            return;

        ImGui.PushID(key);
        string label = EditorCatalog.Name(key);
        JObject propertyOwner = EditorPropertyEdit.Capture(_document, target, key).Owner;
        bool enabled = target.ContainsKey(key) && propertyOwner["disabled"]?[key]?.Value<bool>() != true;
        ImGui.AlignTextToFramePadding();
        if (field.Optional)
        {
            if (Toggle(label, ref enabled))
            {
                FinishNumericEdit();
                if (!target.ContainsKey(key))
                    SetProperty(target, key, FieldDefault(field));
                else
                {
                    var disabled = propertyOwner["disabled"] is JObject flags ? (JObject)flags.DeepClone() : new JObject();
                    disabled[key] = !enabled;
                    SetProperty(propertyOwner, "disabled", disabled);
                }
            }
        }
        else if (label == key)
            WidgetDraw.TextDisabled(label);
        else
            ImGui.TextUnformatted(label);
        float width = ImGui.GetContentRegionAvail().x;
        float column = Math.Clamp(width * .40f, 94 * _uiScale, 175 * _uiScale);
        float labelWidth = ImGui.CalcTextSize(label).x + (field.Optional ? ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x : 0);
        if (labelWidth + ImGui.GetStyle()->ItemSpacing.x <= column && width - column >= 86 * _uiScale)
            ImGui.SameLine(column + ImGui.GetStyle()->WindowPadding.x);
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled((byte)(dependencyDisabled || field.Optional && !enabled ? 1 : 0));
        JToken? value = target[key] ?? field.Default;
        JToken? updated = null;
        string[]? choices = key == "filter" && EditorDocument.Type(target) == "SetFilterAdvanced" ? EditorFilterCatalog.Names
            : EditorCatalog.Options(field) ?? Choices(key, EditorDocument.Type(target));
        if (choices is not null)
        {
            string current = value?.Type == JTokenType.String ? value.Value<string>() ?? "" : choices.FirstOrDefault() ?? "";
            if (ImGui.BeginCombo("##value", L.Option(current)) != 0)
            {
                foreach (string option in choices.Append(current).Distinct())
                    if (ImGui.Selectable(L.Option(option) + "###" + option, (byte)(option == current ? 1 : 0), 0, default) != 0)
                        updated = new JValue(option);
                ImGui.EndCombo();
            }
        }
        else if (field.Type == "Bool")
        {
            bool boolean = value?.Type == JTokenType.Boolean && value.Value<bool>() ||
                           bool.TryParse(value?.ToString(), out bool parsed) && parsed;
            if (Toggle("##value", ref boolean))
                updated = new JValue(boolean);
        }
        else if (field.Type is "Int" or "Float")
        {
            if (key == "floor") field = field with { Minimum = 0, Maximum = _document.LastFloor };
            DrawNumericInput(target, field, value, step: field.Type == "Int");
        }
        else if (field.Type is "Vector2" or "FloatPair" or "Vector2Range" || key == "velocity" && value is JArray)
        {
            if (key == "velocity" && value is JArray { Count: 2 } velocity && velocity[0] is JArray)
                field = field with { Type = "Vector2Range" };
            bool range = field.Type == "Vector2Range";
            for (int axis = 0; axis < (range ? 4 : 2); axis++)
            {
                JToken? part = range ? ((value as JArray)?.ElementAtOrDefault(axis / 2) as JArray)?.ElementAtOrDefault(axis % 2)
                    : (value as JArray)?.ElementAtOrDefault(axis);
                ImGui.TextUnformatted(range ? (axis < 2 ? "Min " : "Max ") + (axis % 2 == 0 ? "X" : "Y") : axis == 0 ? "X" : "Y");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                DrawNumericInput(target, field, part, axis);
            }
        }
        else if (field.Type == "Tile" || value is JArray { Count: 2 } tile && tile[1]?.Type == JTokenType.String)
        {
            JArray? range = value as JArray;
            string relative = range?.ElementAtOrDefault(1)?.Value<string>() ?? "ThisTile";
            DrawNumericInput(target, field with { Type = "Tile" }, range?.ElementAtOrDefault(0), 0);

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##relative", L.Option(relative)) != 0)
            {
                foreach (string option in new[] { "ThisTile", "Start", "End" }.Append(relative).Distinct())
                    if (ImGui.Selectable(L.Option(option) + "###" + option, (byte)(option == relative ? 1 : 0), 0, default) != 0)
                    {
                        FinishNumericEdit();
                        var latest = EditorPropertyEdit.Capture(_document, target, key).Read() as JArray;
                        updated = new JArray(latest?.ElementAtOrDefault(0)?.DeepClone() ?? new JValue(0), option);
                    }
                ImGui.EndCombo();
            }
        }
        else if (field.Type == "MinMaxGradient")
            DrawMinMaxGradient(target, field, value);
        else if (field.Type == "Color")
            DrawColorInput(target, field, value);
        else if (field.Type == "FilterProperties" || key == "filterProperties" && EditorDocument.Type(target) == "SetFilterAdvanced")
            DrawAdvancedFilter(target);
        else if (field.Type is "String" or "File" or "Text" or "LongText" or "LongString" ||
                 field.Type.StartsWith("Enum:", StringComparison.Ordinal))
        {
            bool file = field.Type == "File" || key is "songFilename" or "bgImage" or "bgVideo" or "decorationImage";
            if (file) ImGui.SetNextItemWidth(-38);
            if (EditText("##value", field.Unit ?? "", value?.Type == JTokenType.String ? value.Value<string>() ?? "" : "", false) is { } text)
                updated = new JValue(text);
            if (file)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("…") != 0)
                {
                    try
                    {
                        string? basePath = Path.GetDirectoryName(_document.Path);
                        string[]? paths = NativeFileDialog.NativeFileDialog.GetFileDialog()?.OpenFile(L.T("ChooseAsset"),
                            basePath ?? "", new OpenFileFilter { IncludeAllFiles = true, Filter = [] });
                        if (paths is { Length: > 0 })
                            updated = new JValue(key == "decorationImage" ? _document.ImportDecorationImage(paths[0]) :
                                basePath is not null ? Path.GetRelativePath(basePath, paths[0]) : paths[0]);
                    }
                    catch (Exception error) { ShowError(error); }
                }
            }
        }
        else if (EditText("##json", "JSON", value?.ToString(Formatting.None) ?? "null", false) is { } json)
        {
            try { updated = JToken.Parse(json); }
            catch (JsonReaderException error) { ShowError(error); }
        }

        ImGui.EndDisabled();
        if (updated is not null)
        {
            FinishNumericEdit();
            SetProperty(target, key, updated);
        }
        ImGui.PopID();
    }

    private static JToken FieldDefault(EditorField field) => field.Default?.DeepClone() ?? (field.Type switch
    {
        "Int" or "Float" => new JValue(0), "Bool" => new JValue(false),
        "Vector2" or "FloatPair" => new JArray(0, 0), "Tile" => new JArray(0, "ThisTile"),
        "String" or "File" => new JValue(""), "Vector2Range" => new JArray(new JArray(0, 0), new JArray(0, 0)),
        _ => JValue.CreateNull()
    });

    private static string[]? Choices(string key, string eventType) => key switch
    {
        "speedType" => ["Bpm", "Multiplier"], "ease" => ["Linear", "InQuad", "OutQuad", "InOutQuad", "InCubic", "OutCubic", "InOutCubic", "InSine", "OutSine", "InOutSine"],
        "simulationSpace" => ["Local", "World"], "shapeType" => ["Circle", "Rectangle"], "arcMode" => ["Random", "Loop", "PingPong", "BurstSpread"],
        "targetMode" => ["Start", "Stop", "Clear"], "planets" => ["TwoPlanets", "ThreePlanets"],
        "angleCorrectionDir" => ["Backward", "None", "Forward"],
        "filter" when eventType == "SetFilter" => ["Grayscale", "Sepia", "Invert", "VHS", "EightiesTV", "FiftiesTV",
            "Arcade", "LED", "Rain", "Blizzard", "PixelSnow", "Compression", "Glitch", "Pixelate", "Waves", "Static",
            "Grain", "MotionBlur", "Fisheye", "Aberration", "Drawing", "Neon", "Handheld", "NightVision", "Funk", "Tunnel",
            "Weird3D", "Blur", "BlurFocus", "GaussianBlur", "HexagonBlack", "Posterize", "Sharpen", "Contrast", "EdgeBlackLine",
            "OilPaint", "SuperDot", "WaterDrop", "LightWater", "Petals", "PetalsInstant"],
        "hitsound" => ["Kick", "Shaker", "Sizzle", "Clap", "Hammer", "None"], _ => null
    };

    private static bool IsDataField(EditorField field) => field.Type is not ("ParticlePlayback" or "List" or "Export" or "Button" or "Note");
    private static bool Enabled(JObject item) => !bool.TryParse(item["active"]?.ToString(), out bool active) || active;

    private static double Number(JToken? value, double fallback = 0) =>
        double.TryParse(value is JValue scalar ? Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) : null,
            NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : fallback;

    private static double Component(JArray? value, int index, double fallback = 0) =>
        value is not null && index < value.Count ? Number(value[index], fallback) : fallback;

    private static bool Toggle(string label, ref bool value) => ImGui.Checkbox(label, ref value) != 0;

    private static bool BeginPanel(string title, ref bool open) => ImGui.Begin(title, ref open) != 0;

    private static string? EditText(string id, string hint, string value, bool live, int extraFlags = 0)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = count < 1024 ? stackalloc byte[2048] : new byte[count + 2048];
        int length = Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
        buffer[length] = 0;
        fixed (byte* pointer = buffer)
        {
            int flags = live ? 0 : (int)ImGuiInputTextFlags.ImGuiInputTextFlags_EnterReturnsTrue;
            return ImGui.InputTextWithHint(id, hint, (sbyte*)pointer, (nuint)buffer.Length, flags | extraFlags) != 0
                ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)pointer)
                : null;
        }
    }

    private void DrawShortcuts()
    {
        if (!_showShortcuts)
            return;
        if (BeginPanel(L.T("Shortcuts") + "###shortcuts", ref _showShortcuts))
        {
            foreach (string row in new[] { "HelpTiles", "HelpSelect", "HelpDelete", "HelpClipboard", "HelpTransforms",
                "HelpEvents", "HelpEventPages", "HelpPalette", "HelpCamera", "HelpPlay", "HelpReturn",
                "HelpBookmarks", "HelpLocate", "HelpFile", "HelpUndo" })
                ImGui.TextUnformatted(L.T(row));
        }
        ImGui.End();
    }
}
