using System.Globalization;
using System.Text;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly HashSet<JObject> _selectedDecorations = new(ReferenceEqualityComparer.Instance);
    private bool _hideAllDecorations;
    private bool _lockAllDecorations;
    private string _decorationFilter = "";
    private readonly List<int> _decorationRows = [];
    private (int Version, string Filter, CultureInfo Language)? _decorationRowsKey;
    private string _eventSearch = "";
    private const string DecorationMoreButton = "…###decoration-actions";

    private void DrawSettings()
    {
        if (!_showSettings)
            return;
        if (BeginPanel(SettingsWindow, ref _showSettings) && ImGui.BeginTabBar("##settings-tabs",
                (int)ImGuiTabBarFlags.ImGuiTabBarFlags_FittingPolicyScroll) != 0)
        {
            foreach (string tab in _catalog.SettingsTabs)
            {
                if (ImGui.BeginTabItem(EditorCatalog.Name(tab) + "###" + tab) == 0)
                    continue;
                if (ImGui.BeginChild("##fields", default) != 0)
                {
                    EditorField[] fields = _catalog.SettingsFor(tab);
                    foreach (EditorField field in fields)
                        if (!field.Hidden && IsDataField(field))
                            DrawField(_document.Settings, field, tab, fields);
                }
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
            string visibility = _hideAllDecorations ? L.T("ShowAll") : L.T("HideAll");
            ContinueToolbar(ButtonWidth(visibility));
            if (ImGui.SmallButton(visibility) != 0)
                _hideAllDecorations = !_hideAllDecorations;
            string locking = _lockAllDecorations ? L.T("UnlockAll") : L.T("LockAll");
            ContinueToolbar(ButtonWidth(locking));
            if (ImGui.SmallButton(locking) != 0)
                _lockAllDecorations = !_lockAllDecorations;
            ImGui.Separator();

            if (ImGui.BeginChild("##decoration-list", default) != 0)
            {
                var decorations = _document.Decorations;
                UpdateDecorationRows();
                JObject? moveFrom = null, moveTo = null, remove = null;
                string show = L.T("Show"), hide = L.T("Hide"), unlock = L.T("Unlock"), @lock = L.T("Lock");
                string delete = L.T("DeleteEvent");
                float width = ImGui.GetContentRegionAvail().x, gap = ImGui.GetStyle()->ItemSpacing.x;
                float visibilityWidth = Math.Max(ButtonWidth(show), ButtonWidth(hide));
                float lockWidth = Math.Max(ButtonWidth(unlock), ButtonWidth(@lock));
                float deleteWidth = ButtonWidth(delete);
                float fullActionsWidth = visibilityWidth + lockWidth + deleteWidth + 2 * gap;
                float typeWidth = ImGui.CalcTextSize(L.T("DecorationTypeOther")).x;
                foreach (string type in EditorCatalog.DecorationTypes)
                    typeWidth = Math.Max(typeWidth, ImGui.CalcTextSize(DecorationTypeName(type)).x);
                bool compact = width < fullActionsWidth + typeWidth + 2 * gap + 4 * ImGui.GetFontSize();
                float actionsWidth = compact ? ButtonWidth(DecorationMoreButton) : fullActionsWidth;
                typeWidth = Math.Min(typeWidth, Math.Max(0, width - actionsWidth - 2 * gap - ImGui.GetFontSize()));
                float nameWidth = Math.Max(1, width - actionsWidth - gap - (typeWidth > 0 ? typeWidth + gap : 0));
                ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_FramePadding, WidgetDraw.V2(ImGui.GetStyle()->FramePadding.x, 0));
                ImGuiListClipper clipper = default;
                ImGuiListClipper.ImGuiListClipper_Ctor(&clipper);
                try
                {
                    ImGuiListClipper.Begin(&clipper, _decorationRows.Count, -1);
                    while (ImGuiListClipper.Step(&clipper) != 0)
                        for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                            DrawDecorationRow(_decorationRows[row]);
                }
                finally
                {
                    ImGuiListClipper.Dispose(&clipper);
                    ImGui.PopStyleVar();
                }
                if (moveFrom is not null && moveTo is not null)
                    _document.MoveDecoration(moveFrom, moveTo);
                if (remove is not null)
                    DeleteDecorations([remove]);

                void DrawDecorationRow(int i)
                {
                    var item = (JObject)decorations[i]!;
                    ImGui.PushID(i);
                    float rowStart = ImGui.GetCursorPosX();
                    string name = DecorationName(item);
                    string type = DecorationTypeName(EditorDocument.Type(item));
                    if (ImGui.Selectable(name + "###item", (byte)(_selectedDecorations.Contains(item) ? 1 : 0),
                            0, WidgetDraw.V2(nameWidth, 0)) != 0)
                    {
                        SelectDecoration(item, ImGui.GetIO()->KeyCtrl != 0);
                        CenterOnDecoration(item);
                    }
                    bool showName = ImGui.IsItemHovered() != 0 &&
                        (ImGui.CalcTextSize(name).x > nameWidth || ImGui.CalcTextSize(type).x > typeWidth);
                    _modUi?.DrawContext(SharpFAI.ModApi.MenuSlot.DecorationContext, "###mod-decoration-context",
                        () => SelectDecoration(item));
                    if (ImGui.BeginDragDropSource() != 0)
                    {
                        int original = i;
                        ImGui.SetDragDropPayload("DECORATION", &original, sizeof(int));
                        ImGui.TextUnformatted(name);
                        ImGui.EndDragDropSource();
                    }
                    if (ImGui.BeginDragDropTarget() != 0)
                    {
                        ImGuiPayload* payload = ImGui.AcceptDragDropPayload("DECORATION");
                        if (payload != null && payload->DataSize == sizeof(int))
                        {
                            int from = *(int*)payload->Data;
                            if (from >= 0 && from < decorations.Count && decorations[from] is JObject moved)
                            {
                                moveFrom = moved;
                                moveTo = item;
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }
                    if (showName) WidgetDraw.Tooltip($"{name}\n{type}");
                    if (typeWidth > 0)
                    {
                        ImGui.SameLine();
                        var first = ImGui.GetCursorScreenPos();
                        var end = WidgetDraw.V2(first.x + typeWidth, first.y + ImGui.GetTextLineHeight());
                        ImGui.PushClipRect(in first, in end, 1);
                        ImGui.TextDisabled(type);
                        ImGui.PopClipRect();
                    }
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(rowStart + Math.Max(0, width - actionsWidth));
                    bool hidden = item.Value<bool?>("visible") == false;
                    bool locked = item.Value<bool?>("locked") == true;
                    if (compact)
                    {
                        if (ImGui.Button(DecorationMoreButton, WidgetDraw.V2(actionsWidth, 0)) != 0)
                            ImGui.OpenPopup("##decoration-actions");
                        WidgetDraw.ItemTooltip(L.T("DecorationActions"));
                        if (ImGui.BeginPopup("##decoration-actions") != 0)
                        {
                            DrawActions(menu: true);
                            ImGui.EndPopup();
                        }
                    }
                    else DrawActions(menu: false);
                    ImGui.PopID();

                    void DrawActions(bool menu)
                    {
                        if (Action(hidden ? show : hide, visibilityWidth, menu))
                            _document.Set(item, "visible", hidden ? null : new JValue(false));
                        if (!menu) ImGui.SameLine();
                        if (Action(locked ? unlock : @lock, lockWidth, menu))
                            _document.Set(item, "locked", locked ? null : new JValue(true));
                        if (!menu) ImGui.SameLine();
                        if (Action(delete, deleteWidth, menu, !locked && !_lockAllDecorations))
                            remove = item;
                    }
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();

        static bool Action(string label, float width, bool menu, bool enabled = true)
        {
            if (menu) return ImGui.MenuItem(label, null, 0, (byte)(enabled ? 1 : 0)) != 0;
            ImGui.BeginDisabled((byte)(enabled ? 0 : 1));
            bool pressed = ImGui.Button(label, WidgetDraw.V2(width, 0)) != 0;
            ImGui.EndDisabled();
            return pressed;
        }
    }

    private void UpdateDecorationRows()
    {
        var key = (_document.DecorationListVersion, _decorationFilter, L.Language);
        if (_decorationRowsKey == key)
            return;

        _decorationRowsKey = key;
        _decorationRows.Clear();
        string[] terms = _decorationFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var decorations = _document.Decorations;
        for (int i = 0; i < decorations.Count; i++)
            if (decorations[i] is JObject item && (terms.Length == 0 || MatchesDecoration(item, terms)))
                _decorationRows.Add(i);
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
        string localizedType = EditorCatalog.Name(type);
        string shortType = DecorationTypeName(type);
        string tag = item.Value<string>("tag") ?? "";
        foreach (string term in terms)
        {
            if (term.StartsWith("floor:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(term.AsSpan(6), out int floor) || floor != EditorDocument.Floor(item)) return false;
            }
            else if (term.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                if (!MatchesType(term[5..])) return false;
            }
            else if (term.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                if (!tag.Contains(term[4..], StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                     !tag.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                     !MatchesType(term) &&
                     (!int.TryParse(term, out int floor) || floor != EditorDocument.Floor(item)))
                return false;
        }
        return true;

        bool MatchesType(string term) => type.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            localizedType.Contains(term, StringComparison.OrdinalIgnoreCase) || shortType.Contains(term, StringComparison.OrdinalIgnoreCase);
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

    private static string DecorationTypeName(string type) => L.T(type switch
    {
        "AddDecoration" => "DecorationTypeImage",
        "AddText" => "DecorationTypeText",
        "AddObject" => "DecorationTypeObject",
        "AddParticle" => "DecorationTypeParticle",
        _ => "DecorationTypeOther",
    });

    private void DrawInspector()
    {
        if (!_showInspector)
            return;
        if (BeginPanel(InspectorWindow, ref _showInspector))
        {
            if (_inspectDecoration && _decoration is { Parent: not null } decoration)
            {
                string name = DecorationName(decoration);
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(name);
                ImGui.PopTextWrapPos();
                ImGui.TextDisabled(DecorationTypeName(EditorDocument.Type(decoration)));
                string locate = L.T("LocateDecoration");
                ContinueToolbar(ButtonWidth(locate));
                if (ImGui.SmallButton(locate) != 0)
                    CenterOnDecoration(decoration);
                if (IsSelectedParticle)
                {
                    string edit = L.T("EditParticle");
                    ContinueToolbar(ButtonWidth(edit));
                    if (ImGui.SmallButton(edit) != 0)
                        OpenParticle();
                }
                string delete = L.T("DeleteEvent");
                ContinueToolbar(ButtonWidth(delete));
                ImGui.BeginDisabled((byte)(DecorationLocked(decoration) ? 1 : 0));
                bool remove = ImGui.SmallButton(delete) != 0;
                ImGui.EndDisabled();
                if (remove) DeleteDecorations([decoration]);
                else
                {
                    ImGui.Separator();
                    DrawProperties(decoration, true);
                }
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
                {
                    DrawSelectionStatistics();
                    DrawFloorEvents();
                    DrawModEvents();
                }
            }
            _modUi?.DrawInspectorFields();
        }
        ImGui.End();
    }

    private void DrawSelectionStatistics()
    {
        if (!_applicationSettings.ShowSelectionStatistics || _applicationSettings.SelectionStatistics == Player.SelectionStatisticsFields.None)
            return;

        var (first, last) = FloorRange;
        bool drawn = false;
        if ((_applicationSettings.SelectionStatistics & Player.SelectionStatisticsFields.Count) != 0)
            Show(L.F("SelectionStatCount", last - first + 1));
        if (HasCurrentPlayback && _playback?.EditorTiming is { } timing &&
            TrackSelectionStatistics.Calculate(timing.EntryTimes, timing.FloorBpms, first, last,
                _applicationSettings.ExcludeLastSelectedTileFromTiming) is { } statistics)
        {
            if ((_applicationSettings.SelectionStatistics & Player.SelectionStatisticsFields.Angle) != 0)
                Show(L.F("SelectionStatAngle", statistics.Angle));
            if ((_applicationSettings.SelectionStatistics & Player.SelectionStatisticsFields.Beats) != 0)
                Show(L.F("SelectionStatBeats", statistics.Beats));
            if ((_applicationSettings.SelectionStatistics & Player.SelectionStatisticsFields.Duration) != 0)
                Show(L.F("SelectionStatDuration", statistics.Duration));
        }
        if (drawn)
            ImGui.Separator();

        void Show(string text)
        {
            if (drawn)
                ContinueToolbar(ImGui.CalcTextSize(text).x);
            WidgetDraw.TextDisabled(text);
            drawn = true;
        }
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
        if (!_showPalette && !_showTimeline)
            return;
        bool open = true;
        if (BeginPanel(PaletteWindow, ref open))
        {
            if (ImGui.Button("|<##restart", default) != 0) SelectFloor(0, true);
            WidgetDraw.ItemTooltip(L.T("GoToStart"));
            string preview = L.T(_playbackView == PlaybackView.Preview ? "ReturnToEdit" : "ChartPreview");
            ContinueToolbar(ButtonWidth(preview));
            if (ImGui.Button(preview, default) != 0)
            {
                if (_playbackView == PlaybackView.Preview) ReturnToEdit();
                else SwitchPlaybackView(PlaybackView.Preview);
            }
            string play = L.T(PlaybackCanPause ? "Pause" : "Play");
            ContinueToolbar(ButtonWidth(play));
            if (ImGui.Button(play, default) != 0)
            {
                if (PlaybackCanPause) PauseToPreview();
                else if (_playbackView == PlaybackView.Manual && HasCurrentPlayback) _playback!.Play();
                else SwitchPlaybackView(SelectedPlaybackMode, HeldPlaybackRate);
            }
            WidgetDraw.ItemTooltip(L.T("PlayTooltip"));
            DrawPlaybackModeCheckboxes();
            ContinueToolbar(76 * _uiScale);
            DrawPlaybackSpeed();
            if (_showPalette)
                DrawEventPalette();
            DrawChartTimeline(HasCurrentPlayback ? _playback : null, _playbackView == PlaybackView.Edit);
        }
        ImGui.End();
        if (!open)
            _showPalette = _showTimeline = false;
    }

    private void DrawPlaybackModeCheckboxes()
    {
        bool manual = _manualPlay;
        if (ToolbarCheckbox("Manual", ref manual)) SetManualPlayMode(manual);
        WidgetDraw.ItemTooltip(L.T("ManualTooltip"));

        bool noFail = _noFail;
        if (ToolbarCheckbox("NoFail", ref noFail)) SetNoFail(noFail);
        WidgetDraw.ItemTooltip("Alt+N");
    }

    private static float EventButtonSide => MathF.Ceiling(3 * ImGui.GetTextLineHeight() +
        2 * ImGui.GetStyle()->FramePadding.y);

    private void DrawEventPalette()
    {
        ImGui.Separator();
        for (int i = 0; i < _catalog.Categories.Length; i++)
        {
            string label = EditorCatalog.Name(_catalog.Categories[i].Key);
            if (i > 0)
                ContinueToolbar(ImGui.CalcTextSize(label).x + ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x);
            if (ImGui.RadioButton(label + "###" + _catalog.Categories[i].Key, (byte)(i == _category ? 1 : 0)) != 0)
            {
                _category = i;
                _eventSearch = "";
            }
        }
        ContinueToolbar(105 * _uiScale);
        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().x, 1, 210 * _uiScale));
        if (EditText("##event-search", L.T("SearchEvents"), _eventSearch, true) is { } search)
            _eventSearch = search;

        float eventHeight = _showTimeline
            ? Math.Max(1, ImGui.GetContentRegionAvail().y - TimelineRulerHeight - TimelineFloorHeight -
                ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle()->ItemSpacing.y)
            : 0;
        if (ImGui.BeginChild("##event-buttons", WidgetDraw.V2(0, eventHeight), 0,
                (int)ImGuiWindowFlags.ImGuiWindowFlags_NoScrollbar) != 0)
        {
            float side = Math.Clamp(MathF.Floor(ImGui.GetContentRegionAvail().x), 1, EventButtonSide);
            bool first = true;
            int shortcut = 0;
            foreach (string name in PaletteEvents())
            {
                if (!first) ContinueToolbar(side);
                first = false;
                EditorEvent? definition = _catalog.Find(name);
                bool removesEvent = PaletteToggleTarget(definition) is not null;
                bool disabled = !removesEvent && (definition is null || !CanAddEvent(definition));
                string label = EditorCatalog.Name(name);
                ImGui.BeginDisabled((byte)(disabled ? 1 : 0));
                if (ImGui.Button("###" + name, WidgetDraw.V2(side, side)) != 0)
                    ActivatePaletteEvent(name);
                if (ImGui.IsItemVisible() != 0)
                    DrawEventButtonLabel(label);
                ImGui.EndDisabled();

                // Keep number-key positions stable when an event already occupies its slot.
                string tooltip = label;
                if (_hasFloorSelection && !_inspectDecoration && _rangeAnchor is null &&
                    (_floor != 0 || definition?.AllowFirstFloor != false) && shortcut < 10)
                {
                    shortcut++;
                    if (!disabled)
                        tooltip = removesEvent
                            ? $"{label}\n{shortcut % 10} · {L.T("DeleteEvent")}"
                            : $"{label}\n{shortcut % 10}";
                }
                if (ImGui.IsItemHovered((int)ImGuiHoveredFlags.ImGuiHoveredFlags_AllowWhenDisabled) != 0)
                    WidgetDraw.Tooltip(tooltip);
            }
        }
        ImGui.EndChild();
    }

    private static void DrawEventButtonLabel(string label)
    {
        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        ImVec2 padding = ImGui.GetStyle()->FramePadding;
        float wrapWidth = Math.Max(1, max.x - min.x - 2 * padding.x);
        ImVec2 text = ImGui.CalcTextSize(label, null, 0, wrapWidth);
        ImVec2 origin = WidgetDraw.V2(min.x + Math.Max(padding.x, (max.x - min.x - text.x) / 2),
            min.y + Math.Max(padding.y, (max.y - min.y - text.y) / 2));
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImDrawList.PushClipRect(draw, min, max, 1);
        WidgetDraw.TextWrapped(draw, origin, wrapWidth, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text), label);
        ImDrawList.PopClipRect(draw);
    }

    private IEnumerable<string> PaletteEvents() => _eventSearch.Length == 0
        ? _catalog.Categories[_category].Events
        : _catalog.Categories.SelectMany(category => category.Events).Distinct().Where(name =>
            name.Contains(_eventSearch, StringComparison.OrdinalIgnoreCase) ||
            EditorCatalog.Name(name).Contains(_eventSearch, StringComparison.OrdinalIgnoreCase));

    private bool CanAddEvent(EditorEvent definition) =>
        (_floor != 0 || definition.AllowFirstFloor) &&
        (definition.Decoration || _hasFloorSelection && _document.CanAddEvent(_floor, definition.Type));

    private JObject? PaletteToggleTarget(EditorEvent? definition) =>
        _hasFloorSelection && !_inspectDecoration && _rangeAnchor is null &&
        definition is { Decoration: false } && (_floor != 0 || definition.AllowFirstFloor) &&
        EditorCatalog.IsToggleableEvent(definition.Type)
            ? _document.Events(_floor).FirstOrDefault(item => EditorDocument.Type(item) == definition.Type)
            : null;

    private void ActivatePaletteEvent(string type)
    {
        if (PaletteToggleTarget(_catalog.Find(type)) is { } item)
        {
            CancelPropertyEdits();
            _document.Remove(item);
        }
        else
            AddEvent(type);
    }

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
            DrawField(target, field, type, fields);
        }

        if (type == "SetFilterAdvanced" && fields.All(field => field.Key != "filterProperties"))
            DrawAdvancedFilter(target);
        EditorField[] otherFields = InspectorFields.OtherFields(target, fields, type).ToArray();
        if (otherFields.Length > 0 && ImGui.CollapsingHeader(L.T("OtherFields")) != 0)
            foreach (EditorField field in otherFields)
                DrawField(target, field, type, fields);
        ImGui.PopID();
    }

    private void DrawField(JObject target, EditorField field, string? owner = null, EditorField[]? fields = null)
    {
        string key = field.Key;
        owner ??= EditorDocument.Type(target);
        fields ??= _catalog.Find(owner)?.Fields;
        var condition = InspectorFields.GetState(target, field, fields, owner);
        if (!condition.Visible) return;

        ImGui.PushID(key);
        string label = L.Name(key, owner);
        JObject propertyOwner = EditorPropertyEdit.Capture(_document, target, key).Owner;
        bool enabled = target.ContainsKey(key) && propertyOwner["disabled"]?[key]?.Value<bool>() != true;
        ImGui.BeginDisabled((byte)(condition.Enabled ? 0 : 1));
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
        JToken? value = target[key] ?? field.Default;
        bool vector = field.Type is "Vector2" or "FloatPair" or "Vector2Range" || key == "velocity" && value is JArray;
        float width = ImGui.GetContentRegionAvail().x;
        float column = Math.Clamp(width * .40f, 94 * _uiScale, 175 * _uiScale);
        float labelWidth = ImGui.CalcTextSize(label).x + (field.Optional ? ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x : 0);
        if (!vector && labelWidth + ImGui.GetStyle()->ItemSpacing.x <= column && width - column >= 86 * _uiScale)
            ImGui.SameLine(column + ImGui.GetStyle()->WindowPadding.x);
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled((byte)(field.Optional && !enabled ? 1 : 0));
        JToken? updated = null;
        bool richText = InspectorFields.UsesRichLevelText(owner, key);
        string[]? choices = EditorCatalog.Options(field);
        if (owner == "PlaySound" && key == "hitsound")
            updated = DrawSoundChoice(field, value, choices ?? []);
        else if (choices is { Length: > 0 })
            updated = DrawPropertyChoice("##value", field, value, choices);
        else if (choices is { Length: 0 })
            updated = DrawRawField(value);
        else if (field.Type == "Bool" && (value is null || InspectorFields.TryBoolean(value, out _)))
        {
            InspectorFields.TryBoolean(value, out bool boolean);
            if (Toggle("##value", ref boolean))
                updated = InspectorFields.WithBoolean(value, boolean);
        }
        else if (field.Type is "Int" or "Float" or "Rating")
        {
            if (key == "floor") field = field with { Minimum = 0, Maximum = _document.LastFloor };
            if (field.Type == "Rating") field = field with { Type = "Int", Minimum = field.Minimum ?? 1 };
            if (value is null || value.Type == JTokenType.Null ||
                double.IsFinite(Number(value, double.NaN)))
                DrawNumericInput(target, field, value, step: field.Type == "Int");
            else
                updated = DrawRawField(value);
        }
        else if (vector)
        {
            if (key == "velocity" && value is JArray { Count: 2 } velocity && velocity[0] is JArray)
                field = field with { Type = "Vector2Range" };
            DrawVectorInput(target, field, value as JArray);
        }
        else if (field.Type == "Tile")
            updated = DrawTileField(target, field, value);
        else if (field.Type == "MinMaxGradient")
            DrawMinMaxGradient(target, field, value);
        else if (field.Type == "Color")
            DrawColorInput(target, field, value);
        else if (field.Type == "FilterProperties" || key == "filterProperties" && EditorDocument.Type(target) == "SetFilterAdvanced")
            DrawAdvancedFilter(target);
        else if (InspectorFields.IsMultiline(field) && value?.Type is null or JTokenType.Null or JTokenType.String)
            updated = DrawMultilineField(target, field, value, richText);
        else if (InspectorFields.AssetType(field) is { } assetType)
            updated = DrawAssetField(field, value, assetType);
        else if (field.Type == "String" && value?.Type is null or JTokenType.Null or JTokenType.String)
        {
            if (InspectorFields.TagSource(owner, key) is { } tagSource)
                updated = DrawTagField(field, value, tagSource);
            else if (richText)
                updated = DrawRichLevelTextField(field, value);
            else if (EditText("##value", field.Unit ?? "", value?.Value<string>() ?? "", false) is { } text)
                updated = new JValue(text);
        }
        else
            updated = DrawRawField(value);

        ImGui.EndDisabled();
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
                "HelpEvents", "HelpEventPages", "HelpPalette", "HelpCamera", "HelpFreeAngle", "HelpPlay", "HelpReturn",
                "HelpBookmarks", "HelpLocate", "HelpFile", "HelpUndo", "HelpCanvasOnly" })
                ImGui.TextUnformatted(L.T(row));
        }
        ImGui.End();
    }
}
