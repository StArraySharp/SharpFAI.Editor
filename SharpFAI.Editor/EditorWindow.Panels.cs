using System.Globalization;
using System.Text;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;
using static SharpFAI.Editor.Widgets.EditorControls;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly HashSet<JObject> _selectedDecorations = new(ReferenceEqualityComparer.Instance);
    private bool _hideAllDecorations;
    private bool _lockAllDecorations;
    private string _decorationFilter = "";
    private readonly List<int> _decorationRows = [];
    private (int Version, string Filter, CultureInfo Language)? _decorationRowsKey;
    private string _settingsSearch = "";
    private string _eventSearch = "";
    private (string Query, CultureInfo Language)? _eventSearchKey;
    private string[] _eventSearchResults = [];
    private (int Category, string Search) _eventPaletteScrollKey;
    private readonly EventPaletteIcons _eventPaletteIcons = new();

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
                ImGui.SetNextItemWidth(-1);
                if (EditText("##settings-search", L.T("SearchChartSettings"), _settingsSearch, true) is { } search)
                    _settingsSearch = search;
                if (ImGui.BeginChild("##fields", default) != 0)
                {
                    EditorField[] fields = _catalog.SettingsFor(tab);
                    bool any = false;
                    foreach (EditorField field in fields)
                    {
                        if (!field.Hidden && IsDataField(field) &&
                            (_settingsSearch.Length == 0 ||
                             (field.Key.Contains(_settingsSearch, StringComparison.OrdinalIgnoreCase) ||
                              L.Name(field.Key, tab).Contains(_settingsSearch, StringComparison.CurrentCultureIgnoreCase)) &&
                             InspectorFields.GetState(_document.Settings, field, fields, tab).Visible))
                        {
                            any = true;
                            DrawField(_document.Settings, field, tab, fields);
                        }
                    }
                    if (!any && _settingsSearch.Length > 0) WidgetDraw.TextDisabled(L.T("NoMatchingSettings"));
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
            if (Action("add-decoration", ControlIcon.Add, L.T("AddDecorationAction"), emphasized: true))
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
            if (ImGui.Button(visibility, default) != 0)
                _hideAllDecorations = !_hideAllDecorations;
            string locking = _lockAllDecorations ? L.T("UnlockAll") : L.T("LockAll");
            ContinueToolbar(ButtonWidth(locking));
            if (ImGui.Button(locking, default) != 0)
                _lockAllDecorations = !_lockAllDecorations;
            ImGui.Separator();

            if (ImGui.BeginChild("##decoration-list", default) != 0)
            {
                var decorations = _document.Decorations;
                UpdateDecorationRows();
                if (_decorationRows.Count == 0)
                    WidgetDraw.TextDisabled(L.T(decorations.Count == 0 ? "NoDecorations" : "NoMatchingDecorations"));
                JObject? moveFrom = null, moveTo = null, remove = null;
                string show = L.T("Show"), hide = L.T("Hide"), unlock = L.T("Unlock"), @lock = L.T("Lock");
                string delete = L.T("DeleteEvent");
                float width = ImGui.GetContentRegionAvail().x, gap = ImGui.GetStyle()->ItemSpacing.x;
                ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_FramePadding,
                    WidgetDraw.V2(ImGui.GetStyle()->FramePadding.x, 2 * _uiScale));
                float fullActionsWidth = 3 * IconButtonWidth + 2 * gap;
                float typeWidth = ImGui.CalcTextSize(L.T("DecorationTypeOther")).x;
                foreach (string type in EditorCatalog.DecorationTypes)
                    typeWidth = Math.Max(typeWidth, ImGui.CalcTextSize(DecorationTypeName(type)).x);
                bool compact = width < fullActionsWidth + 2 * gap + 6 * ImGui.GetFontSize();
                float actionsWidth = compact ? IconButtonWidth : fullActionsWidth;
                typeWidth = Math.Min(typeWidth, Math.Max(0, width - actionsWidth - 2 * gap - ImGui.GetFontSize()));
                float nameWidth = Math.Max(1, width - actionsWidth - gap - (typeWidth > 0 ? typeWidth + gap : 0));
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
                            0, WidgetDraw.V2(nameWidth, ImGui.GetFrameHeight())) != 0)
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
                        var end = WidgetDraw.V2(first.x + typeWidth, first.y + ImGui.GetFrameHeight());
                        ImGui.PushClipRect(in first, in end, 1);
                        ImGui.AlignTextToFramePadding();
                        WidgetDraw.TextDisabled(type);
                        ImGui.PopClipRect();
                    }
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(rowStart + Math.Max(0, width - actionsWidth));
                    bool hidden = item.Value<bool?>("visible") == false;
                    bool locked = item.Value<bool?>("locked") == true;
                    if (compact)
                    {
                        if (Action("decoration-actions", ControlIcon.More))
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
                        if (RowAction("visibility", hidden ? show : hide, hidden ? ControlIcon.Hidden : ControlIcon.Visible, menu, hidden))
                            _document.Set(item, "visible", hidden ? null : new JValue(false));
                        if (!menu) ImGui.SameLine();
                        if (RowAction("lock", locked ? unlock : @lock, locked ? ControlIcon.Lock : ControlIcon.Unlock, menu, locked))
                            _document.Set(item, "locked", locked ? null : new JValue(true));
                        if (!menu) ImGui.SameLine();
                        if (RowAction("delete", delete, ControlIcon.Trash, menu, enabled: !locked && !_lockAllDecorations))
                            remove = item;
                    }
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();

        static bool RowAction(string id, string label, ControlIcon icon, bool menu, bool selected = false, bool enabled = true)
        {
            if (menu) return ImGui.MenuItem(label, null, 0, (byte)(enabled ? 1 : 0)) != 0;
            ImGui.BeginDisabled((byte)(enabled ? 0 : 1));
            bool pressed = Action(id, icon, emphasized: selected);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered((int)ImGuiHoveredFlags.ImGuiHoveredFlags_AllowWhenDisabled) != 0)
                WidgetDraw.Tooltip(label);
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
                string type = DecorationTypeName(EditorDocument.Type(decoration));
                if (type != name) WidgetDraw.TextDisabled(type);
                if (Action("locate-decoration", ControlIcon.Center, L.T("LocateDecoration")))
                    CenterOnDecoration(decoration);
                if (IsSelectedParticle)
                {
                    string edit = L.T("EditParticle");
                    ContinueToolbar(ButtonWidth(edit));
                    if (ImGui.Button(edit, default) != 0)
                        OpenParticle();
                }
                ContinueToolbar(IconButtonWidth);
                ImGui.BeginDisabled((byte)(DecorationLocked(decoration) ? 1 : 0));
                bool remove = Action("delete-decoration", ControlIcon.Trash);
                ImGui.EndDisabled();
                WidgetDraw.ItemTooltip(L.T("DeleteEvent"));
                if (remove) DeleteDecorations([decoration]);
                else
                {
                    ImGui.Separator();
                    if (ImGui.BeginChild("##decoration-inspector", default) != 0)
                    {
                        DrawProperties(decoration, true);
                        _modUi?.DrawInspectorFields();
                    }
                    ImGui.EndChild();
                }
            }
            else
            {
                if (Action("previous-floor", ControlIcon.Previous))
                    SelectFloor(_floor - 1, true);
                WidgetDraw.ItemTooltip(L.T("PreviousTile"));
                ContinueToolbar(IconButtonWidth);
                if (Action("next-floor", ControlIcon.Next))
                    SelectFloor(_floor + 1, true);
                WidgetDraw.ItemTooltip(L.T("NextTile"));
                ToolbarReadout(_rangeAnchor is null ? L.F("TileStatus", _floor, _document.LastFloor) :
                    L.F("TileRangeStatus", FloorRange.First, FloorRange.Last, _document.LastFloor), accent: _hasFloorSelection);
                ImGui.Separator();
                if (ImGui.BeginChild("##floor-inspector", default) != 0)
                {
                    if (_hasFloorSelection)
                    {
                        DrawSelectionStatistics();
                        DrawFloorEvents();
                        DrawModEvents();
                    }
                    else WidgetDraw.TextDisabled(L.T("SelectTileHint"));
                    _modUi?.DrawInspectorFields();
                }
                ImGui.EndChild();
            }
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
                ContinueToolbar(ReadoutWidth(text));
            Readout(text);
            drawn = true;
        }
    }

    private void DrawFloorEvents()
    {
        IReadOnlyList<JObject> events = _document.Events(_floor);
        string[] types = events.Select(EditorDocument.Type).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length == 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(L.T("AddEventHint"));
            ImGui.PopTextWrapPos();
            return;
        }
        if (_eventType is null || !types.Contains(_eventType))
        {
            _eventType = types[0];
            _eventPage = 0;
        }
        if (types.Length == 1)
            Readout(EditorCatalog.Name(types[0]), accent: true);
        else
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##floor-event-type", EditorCatalog.Name(_eventType)) != 0)
            {
                foreach (string type in types)
                {
                    bool selected = type == _eventType;
                    if (ImGui.Selectable(EditorCatalog.Name(type) + "###" + type, (byte)(selected ? 1 : 0), 0, default) != 0 && !selected)
                    {
                        _eventType = type;
                        _eventPage = 0;
                    }
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        JObject[] pages = events.Where(item => EditorDocument.Type(item) == _eventType).ToArray();
        _eventPage = Math.Clamp(_eventPage, 0, pages.Length - 1);
        if (pages.Length > 1)
        {
            ImGui.BeginDisabled((byte)(_eventPage == 0 ? 1 : 0));
            if (Action("previous-event", ControlIcon.Previous)) _eventPage--;
            ImGui.EndDisabled();
            WidgetDraw.ItemTooltip(L.T("PreviousEvent"));
            ToolbarReadout($"{_eventPage + 1}/{pages.Length}");
            ContinueToolbar(IconButtonWidth);
            ImGui.BeginDisabled((byte)(_eventPage == pages.Length - 1 ? 1 : 0));
            if (Action("next-event", ControlIcon.Next)) _eventPage++;
            ImGui.EndDisabled();
            WidgetDraw.ItemTooltip(L.T("NextEvent"));
        }

        JObject item = pages[_eventPage];
        bool enabled = Enabled(item);
        if (Toggle(L.T("EventEnabled"), ref enabled))
            _document.Set(item, "active", enabled ? null : new JValue(false));
        ContinueToolbar(IconButtonWidth);
        bool remove = Action("delete-floor-event", ControlIcon.Trash);
        WidgetDraw.ItemTooltip(L.T("DeleteEvent"));
        if (remove)
        {
            _document.Remove(item);
            return;
        }
        ImGui.Separator();
        DrawProperties(item, false);
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

    private uint _footerDock;
    private float? _footerContentHeight;
    private float? _appliedFooterContentHeight;
    private int PlaybackColumnRows => _showTimeline ? 8 : 5;
    private float PlaybackColumnHeight => PlaybackColumnRows * CompactPlaybackRowHeight + 2 * FooterCellInset;
    private static float FooterInset => ImGui.GetStyle()->WindowPadding.x;
    private static float FooterCellInset => ImGui.GetStyle()->CellPadding.x;
    private static float CompactPlaybackRowHeight =>
        ImGui.GetTextLineHeight() + 2 * CompactPlaybackPadding + CompactPlaybackSpacing;
    private static float CompactPlaybackPadding => MathF.Floor(Math.Min(ImGui.GetStyle()->FramePadding.y, ImGui.GetFontSize() * .23f));
    private static float CompactPlaybackSpacing => MathF.Floor(Math.Min(ImGui.GetStyle()->ItemSpacing.y, ImGui.GetFontSize() * .24f));

    private void DrawPalette()
    {
        UpdateTimelineWaveform();
        if (!_showPalette && !_showTimeline)
            return;
        bool open = true;
        const ImGuiTableFlags layoutFlags = ImGuiTableFlags.ImGuiTableFlags_BordersOuter |
            ImGuiTableFlags.ImGuiTableFlags_BordersInnerV |
            ImGuiTableFlags.ImGuiTableFlags_NoSavedSettings;
        float inset = FooterInset;
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowPadding, WidgetDraw.V2(inset, inset));
        bool visible = BeginPanel(PaletteWindow, ref open);
        ImGui.PopStyleVar();
        if (visible)
        {
            ImVec2 available = ImGui.GetContentRegionAvail();
            float transportWidth = Math.Max(102 * _uiScale, Math.Max(
                Math.Max(ActionWidth(L.T("Play")), ActionWidth(L.T("Pause"))),
                Math.Max(ButtonWidth(L.T("Manual")), ButtonWidth(L.T("NoFail")))));
            if (_showTimeline)
                transportWidth = Math.Max(transportWidth, Math.Max(ActionWidth(L.T("Metronome")),
                    Math.Max(ActionWidth(L.T("TimelineFit")),
                        Math.Max(ActionWidth(L.T("TimelineLoopRange")), ActionWidth(L.T("TimelineStopLoop"))))));
            float cellInset = FooterCellInset;
            ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_CellPadding, WidgetDraw.V2(cellInset, cellInset));
            if (ImGui.BeginTable("##event-footer", 2, (int)layoutFlags,
                    WidgetDraw.V2(0, Math.Max(0, available.y))) != 0)
            {
                ImGui.TableSetupColumn("##transport", (int)ImGuiTableColumnFlags.ImGuiTableColumnFlags_WidthFixed,
                    Math.Min(transportWidth, Math.Max(1, available.x * .42f)));
                ImGui.TableSetupColumn("##events-and-timeline", (int)ImGuiTableColumnFlags.ImGuiTableColumnFlags_WidthStretch);
                ImGui.TableNextColumn();
                // Measure the compact rows so stretching the controls doesn't grow the dock again.
                float transportBottom = ImGui.GetCursorPosY() + PlaybackColumnRows * CompactPlaybackRowHeight;
                DrawPlaybackColumn();
                ImGui.TableNextColumn();
                float contentHeightAdjustment = DrawPaletteContents(out bool timelineReady);
                float eventsBottom = ImGui.GetCursorPosY() + contentHeightAdjustment;
                ImGui.EndTable();

                if (ImGui.GetWindowDockID() == _footerDock)
                {
                    float padding = inset + cellInset;
                    _footerContentHeight = MathF.Ceiling(Math.Max(transportBottom, eventsBottom) + padding);
                    if (_preserveRestoredFooterSize)
                    {
                        bool transportFits = transportBottom + padding <= ImGui.GetWindowHeight();
                        if (transportFits && _showTimeline) _appliedFooterContentHeight = _footerContentHeight;
                        _preserveRestoredFooterSize = transportFits && _showTimeline && !timelineReady &&
                            _playback?.LoadError is null;
                    }
                }
            }
            ImGui.PopStyleVar();
        }
        ImGui.End();
        if (!open)
            _showPalette = _showTimeline = false;
    }

    private float DrawPaletteContents(out bool timelineReady)
    {
        var timelineRenderer = _showTimeline && HasCurrentPlayback &&
            _playback is { EditorTiming: not null } renderer && double.IsFinite(renderer.Duration)
                ? renderer : null;
        bool usingSnapshot = _showTimeline && timelineRenderer is null && HasTimelineSnapshot;
        int trackCount = usingSnapshot ? 1 + _timelineEventTracks.Length : TimelineTrackCount;
        int timelineVersion = _document.PlaybackVersion;
        float minimumTimelineHeight = _showTimeline ? TimelineHeight(trackCount) : 0;
        float eventHeightAdjustment = _showPalette ? DrawEventPalette(minimumTimelineHeight) : 0;
        if (timelineVersion != _document.PlaybackVersion)
        {
            timelineRenderer = null;
            if (HasTimelineSnapshot) trackCount = 1 + _timelineEventTracks.Length;
        }
        float extraTimelineHeight = DrawChartTimeline(timelineRenderer, _playbackView == PlaybackView.Edit, trackCount);
        timelineReady = timelineRenderer is not null;
        return eventHeightAdjustment - extraTimelineHeight;
    }

    private void DrawPlaybackColumn()
    {
        ImGuiStyle* style = ImGui.GetStyle();
        float padding = CompactPlaybackPadding;
        float spacing = CompactPlaybackSpacing;
        if (_showTimeline)
        {
            int rows = PlaybackColumnRows;
            float spareHeight = Math.Max(0, ImGui.GetContentRegionAvail().y - style->CellPadding.y -
                rows * CompactPlaybackRowHeight);
            float extraPadding = MathF.Floor(Math.Min(3 * _uiScale, spareHeight / (2 * rows)));
            padding += extraPadding;
            spacing += MathF.Floor((spareHeight - 2 * rows * extraPadding) / rows);
        }
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_FramePadding,
            WidgetDraw.V2(style->FramePadding.x, padding));
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_ItemSpacing,
            WidgetDraw.V2(style->ItemSpacing.x, spacing));
        if (Action("restart", ControlIcon.Restart, stretch: true)) SelectFloor(0, true);
        WidgetDraw.ItemTooltip(L.T("GoToStart"));
        string play = L.T(PlaybackCanPause ? "Pause" : "Play");
        if (Action("transport-play", PlaybackCanPause ? ControlIcon.Pause : ControlIcon.Play, play,
                emphasized: true, stretch: true))
        {
            if (PlaybackCanPause) PauseToPreview();
            else if (_playbackView == PlaybackView.Manual && HasCurrentPlayback) _playback!.Play();
            else SwitchPlaybackView(SelectedPlaybackMode, HeldPlaybackRate);
        }
        WidgetDraw.ItemTooltip(L.T("PlayTooltip"));

        bool manual = _manualPlay;
        if (EditorControls.Toggle(L.T("Manual"), ref manual, stretch: true)) SetManualPlayMode(manual);
        WidgetDraw.ItemTooltip(L.T("ManualTooltip"));

        bool noFail = _noFail;
        if (EditorControls.Toggle(L.T("NoFail"), ref noFail, stretch: true)) SetNoFail(noFail);
        WidgetDraw.ItemTooltip("Alt+N");

        ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().x - IconButtonWidth - style->ItemSpacing.x));
        DrawPlaybackSpeed();
        ImGui.SameLine();
        DrawQuickAudition();
        if (_showTimeline) DrawTimelinePlaybackButtons();
        ImGui.PopStyleVar(2);
    }

    private void DrawTimelinePlaybackButtons()
    {
        if (Action("timeline-metronome", ControlIcon.Metronome, L.T("Metronome"), emphasized: _metronomeEnabled, stretch: true))
        {
            _metronomeEnabled = !_metronomeEnabled;
            if (_playback is { } renderer && (HasCurrentPlayback || !_metronomeEnabled))
                UpdateEditorMetronome(renderer);
        }
        WidgetDraw.ItemTooltip(L.T(_metronomeEnabled ? "MetronomeOn" : "MetronomeOff"));

        ImGui.BeginDisabled((byte)(HasTimelineSnapshot ? 0 : 1));
        if (Action("timeline-fit-song", ControlIcon.Fit, L.T("TimelineFit"), stretch: true))
        {
            _chartTimeline.ViewStart = 0;
            _chartTimeline.ViewLength = Math.Max(Timeline.MinimumViewLength, _timelineDuration);
        }
        ImGui.EndDisabled();

        bool hasRange = HasTimelineSnapshot && _chartTimeline.PlaybackRange is { IsValid: true };
        bool canLoop = hasRange && HasCurrentPlayback && _playbackView != PlaybackView.Manual;
        bool looping = _loopingSelection is not null;
        ImGui.BeginDisabled((byte)(canLoop ? 0 : 1));
        if (Action("timeline-loop-range", ControlIcon.Loop, L.T(looping ? "TimelineStopLoop" : "TimelineLoopRange"),
                emphasized: looping, stretch: true) && _chartTimeline.PlaybackRange is { } range)
            ToggleTimelineLoop(range);
        ImGui.EndDisabled();
        if (!hasRange && ImGui.IsItemHovered((int)ImGuiHoveredFlags.ImGuiHoveredFlags_AllowWhenDisabled) != 0)
            WidgetDraw.Tooltip(L.T("TimelineRangeHelp"));
    }

    private float EventTileSide => MathF.Ceiling(Math.Max(48 * _uiScale,
        2 * ImGui.GetTextLineHeight() + ImGui.GetStyle()->FramePadding.y));

    private float DrawEventPalette(float timelineHeight)
    {
        float searchWidth = Math.Clamp(ImGui.GetContentRegionAvail().x * .34f, 1,
            Math.Max(155 * _uiScale, ButtonWidth(L.T("SearchEvents"))));
        DrawEventCategories(searchWidth);
        ContinueToolbar(searchWidth);
        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().x, 1, searchWidth));
        if (EditText("##event-search", L.T("SearchEvents"), _eventSearch, true) is { } search)
            _eventSearch = search;

        var events = PaletteEvents();
        if (events.Length == 0)
        {
            WidgetDraw.TextDisabled(L.T("NoMatchingEvents"));
            return 0;
        }

        ImGuiStyle* style = ImGui.GetStyle();
        ImVec2 spacing = style->ItemSpacing;
        float preferredSide = EventTileSide;
        float width = Math.Max(1, ImGui.GetContentRegionAvail().x);
        int ColumnCount(float available) =>
            Math.Clamp((int)((available + spacing.x) / (preferredSide + spacing.x)), 1, events.Length);
        int columns = ColumnCount(width);
        int fullRows = (events.Length + columns - 1) / columns;
        float reserved = timelineHeight + ImGui.GetFrameHeightWithSpacing() + 2 * spacing.y;
        float roomForEvents = ImGui.GetContentRegionAvail().y - reserved + spacing.y;
        float tileSide = MathF.Floor(Math.Min(width, preferredSide));
        float rowPitch = tileSide + spacing.y;
        int rowBudget = timelineHeight > 0 ? Math.Max(1, (int)(roomForEvents / rowPitch)) : fullRows;
        bool scroll = rowBudget < fullRows;
        if (scroll)
        {
            width = Math.Max(1, width - style->ScrollbarSize);
            columns = ColumnCount(width);
            fullRows = (events.Length + columns - 1) / columns;
            tileSide = MathF.Floor(Math.Min(width, preferredSide));
            rowPitch = tileSide + spacing.y;
            rowBudget = Math.Max(1, (int)(roomForEvents / rowPitch));
        }
        float eventHeight = Math.Min(rowBudget, fullRows) * rowPitch - spacing.y;
        float preferredHeight = fullRows * rowPitch - spacing.y;
        if (!_showTimeline && fullRows == 1)
        {
            // Enlarge the squares into the room alongside the playback column without growing the dock.
            float rowWidth = (width - (events.Length - 1) * spacing.x) / events.Length;
            float expandedSide = MathF.Floor(Math.Min(Math.Min(rowWidth, 84 * _uiScale),
                ImGui.GetContentRegionAvail().y - style->CellPadding.y));
            tileSide = eventHeight = Math.Max(tileSide, expandedSide);
        }
        int childFlags = scroll ? (int)ImGuiWindowFlags.ImGuiWindowFlags_AlwaysVerticalScrollbar : 0;
        if (_eventPaletteScrollKey != (_category, _eventSearch) || !scroll)
            ImGui.SetNextWindowScroll(WidgetDraw.V2(-1, 0));
        _eventPaletteScrollKey = (_category, _eventSearch);
        if (ImGui.BeginChild("##event-buttons", WidgetDraw.V2(0, eventHeight), 0, childFlags) != 0)
        {
            int shortcut = 0;
            for (int index = 0; index < events.Length; index++)
            {
                if (index % columns != 0) ImGui.SameLine();
                string name = events[index];
                EditorEvent? definition = _catalog.Find(name);
                bool removesEvent = PaletteToggleTarget(definition) is not null;
                bool disabled = !removesEvent && (definition is null || !CanAddEvent(definition));
                string label = EditorCatalog.Name(name);

                // Keep number-key positions stable when an event already occupies its slot.
                int? key = null;
                if (_hasFloorSelection && !_inspectDecoration && _rangeAnchor is null &&
                    (_floor != 0 || definition?.AllowFirstFloor != false) && shortcut < 10)
                {
                    shortcut++;
                    if (!disabled) key = shortcut % 10;
                }
                ImGui.BeginDisabled((byte)(disabled ? 1 : 0));
                if (EventTile(name, label, _eventPaletteIcons.Get(name), removesEvent, tileSide))
                    ActivatePaletteEvent(name);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered((int)ImGuiHoveredFlags.ImGuiHoveredFlags_AllowWhenDisabled) != 0)
                {
                    string tooltip = removesEvent ? label + "\n" + L.T("DeleteEvent") : label;
                    if (key is not null) tooltip += "\n" + key;
                    WidgetDraw.Tooltip(tooltip);
                }
            }
        }
        ImGui.EndChild();
        return preferredHeight - eventHeight;
    }

    private void DrawEventCategories(float searchWidth)
    {
        var categories = _catalog.Categories;
        float width = (categories.Length - 1) * ImGui.GetStyle()->ItemSpacing.x;
        foreach (var category in categories) width += ButtonWidth(EditorCatalog.Name(category.Key));
        if (width + ImGui.GetStyle()->ItemSpacing.x + searchWidth <= ImGui.GetContentRegionAvail().x)
        {
            for (int i = 0; i < categories.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                string key = categories[i].Key;
                bool selected = i == _category && _eventSearch.Length == 0;
                if (selected) PushSelected();
                bool pressed = ImGui.Button(EditorCatalog.Name(key) + "###category-" + key, default) != 0;
                if (selected) ImGui.PopStyleColor();
                if (pressed) SelectCategory(i);
            }
            return;
        }

        string current = EditorCatalog.Name(categories[_category].Key);
        ImGui.SetNextItemWidth(Math.Clamp(ButtonWidth(current) + ImGui.GetFrameHeight(), 1,
            Math.Max(1, ImGui.GetContentRegionAvail().x)));
        if (ImGui.BeginCombo("##event-category", current) == 0)
        {
            WidgetDraw.ItemTooltip(L.T("EventCategory"));
            return;
        }
        for (int i = 0; i < categories.Length; i++)
        {
            string key = categories[i].Key;
            bool selected = i == _category;
            if (ImGui.Selectable(EditorCatalog.Name(key) + "###" + key, (byte)(selected ? 1 : 0), 0, default) != 0)
                SelectCategory(i);
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private void SelectCategory(int index)
    {
        _category = index;
        _eventSearch = "";
    }

    private string[] PaletteEvents()
    {
        if (_eventSearch.Length == 0) return _catalog.Categories[_category].Events;

        string query = _eventSearch;
        var key = (query, L.Language);
        if (_eventSearchKey != key)
        {
            _eventSearchResults = _catalog.Categories.SelectMany(category => category.Events).Distinct().Where(name =>
                name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                EditorCatalog.Name(name).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            _eventSearchKey = key;
        }
        return _eventSearchResults;
    }

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

        if (type == "SetSpeed")
            DrawSetSpeedEffectiveBpm(target);
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
        float labelWidth = ImGui.CalcTextSize(label).x + (field.Optional ? ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x : 0);
        if (!vector) AlignScalarPropertyValue(labelWidth);
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

    private void AlignScalarPropertyValue(float labelWidth)
    {
        float width = ImGui.GetContentRegionAvail().x;
        float column = Math.Clamp(width * .40f, 94 * _uiScale, 175 * _uiScale);
        if (labelWidth + ImGui.GetStyle()->ItemSpacing.x <= column && width - column >= 86 * _uiScale)
            ImGui.SameLine(column + ImGui.GetStyle()->WindowPadding.x);
    }

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
