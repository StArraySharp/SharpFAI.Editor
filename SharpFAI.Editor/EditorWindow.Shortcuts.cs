using ImGui4NET;
using Newtonsoft.Json.Linq;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private int? _rangeAnchor;
    private bool _hasFloorSelection = true;
    private bool _shortcutAuto = true;
    private bool _shortcutNoFail;
    private PlaybackRate? _queuedRateShortcut;
    private KeyModifiers? _spacePressModifiers;
    private float _uiScale = 1;
    private float _arbitraryAngle = 180;
    private string _commentSearch = "";
    private bool _openFindComment;
    private EditorDocument.TileClipboard? _tileClipboard;
    private JObject[] _eventClipboard = [];
    private JObject[] _decorationClipboard = [];

    private (int First, int Last) FloorRange =>
        (Math.Min(_rangeAnchor ?? _floor, _floor), Math.Max(_rangeAnchor ?? _floor, _floor));

    private void HandleShortcuts()
    {
        ImGuiIO* io = ImGui.GetIO();
        KeyModifiers? spaceModifiers = _spacePressModifiers;
        _spacePressModifiers = null;
        if (_playbackView == PlaybackView.Manual || io->WantTextInput != 0 ||
            ImGui.IsAnyItemActive() != 0 || _pending is not null || _error is not null ||
            _openFindFloor || _openFindComment ||
            ImGui.IsPopupOpen((string?)null, (int)ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupId |
                (int)ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupLevel) != 0)
            return;

        bool shift = io->KeyShift != 0;
        bool alt = io->KeyAlt != 0;
        if (spaceModifiers is { } modifiers)
        {
            if ((modifiers & (KeyModifiers.Control | KeyModifiers.Super)) != 0)
                _queuedRateShortcut = PlaybackRate.Configured;
            else if ((modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0)
                _queuedRateShortcut = PlaybackRate.Normal;
            else if (modifiers == KeyModifiers.Shift && _playbackView != PlaybackView.Auto &&
                     !_inspectDecoration && _hasFloorSelection && _rangeAnchor is null)
                InsertTile((_document.IncomingAngle(_floor) + 180) % 360);
            return;
        }
        if (io->KeyCtrl != 0 || io->KeySuper != 0)
        {
            HandleControlShortcuts(shift, alt);
            return;
        }
        if (alt)
        {
            if (Pressed(ImGuiKey.ImGuiKey_Semicolon)) OpenFindFloor();
            if (Pressed(ImGuiKey.ImGuiKey_F)) _openFindComment = true;
            if (Pressed(ImGuiKey.ImGuiKey_B)) ToggleBookmark();
            if (Pressed(ImGuiKey.ImGuiKey_LeftArrow)) JumpToBookmark(previous: true);
            if (Pressed(ImGuiKey.ImGuiKey_RightArrow)) JumpToBookmark(previous: false);
            if (Pressed(ImGuiKey.ImGuiKey_A)) ToggleShortcutAutoplay();
            if (Pressed(ImGuiKey.ImGuiKey_N)) ToggleShortcutNoFail();
            return;
        }

        if (Pressed(ImGuiKey.ImGuiKey_LeftBracket)) ChangeEventPage(-1, shift);
        if (Pressed(ImGuiKey.ImGuiKey_RightBracket)) ChangeEventPage(1, shift);
        if (Pressed(ImGuiKey.ImGuiKey_UpArrow)) { if (shift) CycleEventInstance(-1); else CycleEvent(-1); }
        if (Pressed(ImGuiKey.ImGuiKey_DownArrow)) { if (shift) CycleEventInstance(1); else CycleEvent(1); }
        if (Pressed(ImGuiKey.ImGuiKey_LeftArrow)) MoveFloorSelection((shift ? _floor : FloorRange.First) - 1, shift);
        if (Pressed(ImGuiKey.ImGuiKey_RightArrow)) MoveFloorSelection((shift ? _floor : FloorRange.Last) + 1, shift);
        if (Pressed(ImGuiKey.ImGuiKey_Home)) MoveFloorSelection(0, shift);
        if (Pressed(ImGuiKey.ImGuiKey_End)) MoveFloorSelection(_document.LastFloor, shift);
        if (Pressed(ImGuiKey.ImGuiKey_Minus) || Pressed(ImGuiKey.ImGuiKey_KeypadSubtract)) ZoomByKey(-1);
        if (Pressed(ImGuiKey.ImGuiKey_Equal) || Pressed(ImGuiKey.ImGuiKey_KeypadAdd)) ZoomByKey(1);
        if (Pressed(ImGuiKey.ImGuiKey_P))
        {
            StartFromShortcut();
            return;
        }
        if (_playbackView == PlaybackView.Auto)
            return;
        if (Pressed(ImGuiKey.ImGuiKey_Escape))
        {
            if (_playbackView == PlaybackView.Edit)
            {
                _hasFloorSelection = _inspectDecoration = false;
                _rangeAnchor = null;
                _decoration = null;
                _selectedDecorations.Clear();
            }
            return;
        }
        if (Pressed(ImGuiKey.ImGuiKey_Delete)) DeleteSelection(backwards: false);
        if (Pressed(ImGuiKey.ImGuiKey_Backspace)) DeleteSelection(backwards: true);

        if (_inspectDecoration || !_hasFloorSelection || _rangeAnchor is not null)
            return;

        int number = PressedNumber();
        if (number >= 0)
        {
            string[] events = AvailableEvents().ToArray();
            if (number < events.Length)
                AddEvent(events[number]);
            return;
        }
        if (Pressed(ImGuiKey.ImGuiKey_Tab)) { InsertTile(999); return; }
        if (Pressed(ImGuiKey.ImGuiKey_Enter) || Pressed(ImGuiKey.ImGuiKey_KeypadEnter))
        {
            if (_showArbitraryAngle)
                InsertArbitraryAngle();
            return;
        }
        if (Pressed(ImGuiKey.ImGuiKey_F5)) { InsertPolygon(5, shift); return; }
        if (Pressed(ImGuiKey.ImGuiKey_F7)) { InsertPolygon(7, shift); return; }
        foreach (var direction in Directions)
            if (Pressed(direction.Key)) { InsertTile(direction.AngleFor(FineDirections)); return; }
        if (Pressed(ImGuiKey.ImGuiKey_X)) InsertTile(270);
    }

    private void HandleControlShortcuts(bool shift, bool alt)
    {
        if (Pressed(ImGuiKey.ImGuiKey_N)) Confirm(() => SetDocument(new()));
        if (Pressed(ImGuiKey.ImGuiKey_O)) Confirm(Open);
        if (Pressed(ImGuiKey.ImGuiKey_S)) Save(shift);
        if (Pressed(ImGuiKey.ImGuiKey_Z)) { CancelPropertyEdits(); if (shift) Redo(); else Undo(); }
        if (Pressed(ImGuiKey.ImGuiKey_Y)) { CancelPropertyEdits(); Redo(); }
        if (Pressed(ImGuiKey.ImGuiKey_H)) _showShortcuts = !_showShortcuts;
        if (Pressed(ImGuiKey.ImGuiKey_Q)) RequestClose();
        if (Pressed(ImGuiKey.ImGuiKey_F)) { if (shift) OpenFindFloor(); else _showBrickNumbers = !_showBrickNumbers; }
        if (Pressed(ImGuiKey.ImGuiKey_P)) _queuedRateShortcut = PlaybackRate.Half;
        if (Pressed(ImGuiKey.ImGuiKey_LeftArrow)) MoveFloorSelection(0, shift);
        if (Pressed(ImGuiKey.ImGuiKey_RightArrow)) MoveFloorSelection(_document.LastFloor, shift);

        int number = PressedNumber();
        int[] categories = AvailableCategories().ToArray();
        if (!shift && !alt && number >= 0 && number < categories.Length)
        {
            _category = categories[number];
            _eventSearch = "";
        }
        if (Pressed(ImGuiKey.ImGuiKey_Minus)) ScaleUi(-0.1f);
        if (Pressed(ImGuiKey.ImGuiKey_Equal)) ScaleUi(0.1f);
        if (Pressed(ImGuiKey.ImGuiKey_C)) CopySelection(shift || alt, alt, cut: false);
        if (Pressed(ImGuiKey.ImGuiKey_X)) CopySelection(shift || alt, alt, cut: true);
        if (Pressed(ImGuiKey.ImGuiKey_V)) PasteSelection(events: shift, includeDecorations: !alt);
        if (Pressed(ImGuiKey.ImGuiKey_D) && _inspectDecoration)
            _document.PasteDecorations(_selectedDecorations);
        if (!alt && Pressed(ImGuiKey.ImGuiKey_L))
        {
            if (_inspectDecoration)
                _document.SetDecorationLocks(_selectedDecorations,
                    _selectedDecorations.Any(item => item.Value<bool?>("locked") != true));
            else
                TransformSelection(shift ? a => -a : a => 180 - a);
        }
        if (Pressed(ImGuiKey.ImGuiKey_Comma)) TransformSelection(a => a + 90);
        if (Pressed(ImGuiKey.ImGuiKey_Period)) TransformSelection(a => a - 90);
        if (Pressed(ImGuiKey.ImGuiKey_Slash)) TransformSelection(a => a + 180);
        if (_hasFloorSelection && !_inspectDecoration)
        {
            int first = FloorRange.First;
            if (Pressed(ImGuiKey.ImGuiKey_Delete))
                DeleteFloorRange(first + 1, _document.LastFloor, first);
            if (Pressed(ImGuiKey.ImGuiKey_Backspace))
                DeleteFloorRange(1, first, 0);
        }
    }

    private static int PressedNumber()
    {
        for (int i = 0; i < 10; i++)
            if (Pressed((ImGuiKey)((int)ImGuiKey.ImGuiKey_0 + ((i + 1) % 10))))
                return i;
        return -1;
    }

    private IEnumerable<string> AvailableEvents() => PaletteEvents().Where(type =>
        _floor != 0 || _catalog.Find(type)?.AllowFirstFloor != false);

    private IEnumerable<int> AvailableCategories() => Enumerable.Range(0, _catalog.Categories.Length)
        .Where(index => _catalog.Categories[index].Events.Any(type =>
            _floor != 0 || _catalog.Find(type)?.AllowFirstFloor != false));

    private void MoveFloorSelection(int floor, bool extend)
    {
        int anchor = _rangeAnchor ?? _floor;
        SelectFloor(floor, true);
        if (extend && anchor != _floor)
            _rangeAnchor = anchor;
    }

    private void CopySelection(bool events, bool allOfType, bool cut)
    {
        if (_inspectDecoration)
        {
            _decorationClipboard = _document.Decorations.OfType<JObject>().Where(_selectedDecorations.Contains)
                .Select(item => (JObject)item.DeepClone()).ToArray();
            if (cut) DeleteSelection(backwards: true);
            return;
        }
        if (!_hasFloorSelection)
            return;
        if (events)
        {
            JObject[] instances = _document.Events(_floor).Where(item => EditorDocument.Type(item) == _eventType).ToArray();
            IEnumerable<JObject> selected = allOfType ? instances : instances.Skip(_eventPage).Take(1);
            _eventClipboard = selected.Select(item => (JObject)item.DeepClone()).ToArray();
            if (cut) _document.RemoveItems(selected, decoration: false);
            return;
        }
        var (first, last) = FloorRange;
        _tileClipboard = _document.CopyTiles(first, last);
        if (cut && first > 0)
            DeleteFloorRange(first, last, first - 1);
    }

    private void PasteSelection(bool events, bool includeDecorations)
    {
        if (_inspectDecoration)
        {
            _document.PasteDecorations(_decorationClipboard);
            return;
        }
        if (!_hasFloorSelection)
            return;
        if (events)
        {
            _document.PasteEvents(_floor, _eventClipboard, decoration: false);
            return;
        }
        if (_pathLocked || _tileClipboard is not { Angles.Length: > 0 } clipboard)
            return;
        _pendingTiles.Clear();
        _document.PasteTiles(_floor, clipboard, includeDecorations);
        SelectFloor(_floor + clipboard.Angles.Length, true);
    }

    private void DeleteFloorRange(int first, int last, int select)
    {
        if (_pathLocked || first < 1 || last > _document.LastFloor || first > last)
            return;
        _pendingTiles.Clear();
        _document.DeleteTiles(first, last);
        SelectFloor(select, true);
    }

    private void TransformSelection(Func<double, double> transform)
    {
        if (_pathLocked || !_hasFloorSelection || _inspectDecoration)
            return;
        _pendingTiles.Clear();
        var (first, last) = FloorRange;
        _document.TransformTiles(first, last, transform);
    }

    private void CycleEventInstance(int direction)
    {
        string? initialType = _eventType;
        int previous = _eventPage;
        ChangeEventPage(direction, false);
        if (previous == _eventPage)
        {
            CycleEvent(direction);
            if (direction < 0 && _eventType != initialType)
                ChangeEventPage(1, true);
        }
    }

    private void ZoomByKey(int direction)
    {
        if (_playback is { Level: not null } renderer)
            renderer.Zoom(direction, new(renderer.Camera.ViewportWidth * 0.5f, renderer.Camera.ViewportHeight * 0.5f));
    }

    private void ScaleUi(float change)
    {
        _uiScale = Math.Clamp(_uiScale + change, 0.75f, 2f);
        ImGui.GetStyle()->FontScaleMain = _uiScale;
    }

    private void InsertArbitraryAngle()
    {
        if (!float.IsFinite(_arbitraryAngle))
            return;
        bool reverse = _document.ActionsOfType("Twirl").Count(item => EditorDocument.Floor(item) <= _floor && Enabled(item)) % 2 != 0;
        double delta = (180 - _arbitraryAngle) * (reverse ? -1 : 1);
        InsertTile((_document.IncomingAngle(_floor) + delta + 360) % 360);
    }

    private void InsertPolygon(int sides, bool reverse)
    {
        double heading = _document.IncomingAngle(_floor);
        double turn = (reverse ? -1 : 1) * 360.0 / sides;
        double[] angles = new double[sides];
        for (int i = 0; i < sides; i++)
        {
            heading = (heading + turn + 360) % 360;
            angles[i] = heading;
        }
        InsertTiles(angles);
    }

    private void DrawFindComment()
    {
        string popup = L.T("FindComment") + "###find-comment";
        if (_openFindComment)
        {
            ImGui.OpenPopup(popup);
            _openFindComment = false;
        }
        if (ImGui.BeginPopupModal(popup) == 0)
            return;
        if (EditText("##comment-search", L.T("SearchComment"), _commentSearch, true) is { } search)
            _commentSearch = search;
        if (ImGui.BeginChild("##comments", WidgetDraw.V2(340, 195)) != 0)
        {
            int i = 0;
            foreach (JObject comment in _document.ActionsOfType("EditorComment"))
            {
                string text = comment.Value<string>("comment") ?? comment.Value<string>("text") ?? "";
                if (!text.Contains(_commentSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                int floor = EditorDocument.Floor(comment);
                ImGui.PushID(i++);
                if (ImGui.Selectable($"{floor}: {text}", 0, 0, default) != 0)
                {
                    SelectFloor(floor, true);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        if (ImGui.Button(L.T("Close"), default) != 0 || Pressed(ImGuiKey.ImGuiKey_Escape))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void ToggleBookmark()
    {
        if (!_hasFloorSelection || _inspectDecoration)
            return;
        JObject? existing = _document.Events(_floor).FirstOrDefault(item => EditorDocument.Type(item) == "Bookmark");
        if (existing is not null) _document.Remove(existing);
        else AddEvent("Bookmark");
    }

    private void JumpToBookmark(bool previous)
    {
        var indices = _document.ActionsOfType("Bookmark").Where(Enabled).Select(EditorDocument.Floor);
        int next = previous ? indices.Where(index => index < _floor).DefaultIfEmpty(_floor).Max() :
            indices.Where(index => index > _floor).DefaultIfEmpty(_floor).Min();
        SelectFloor(next, true);
    }

    private void StartFromShortcut(PlaybackRate rate = PlaybackRate.Normal)
    {
        if (_playbackView == PlaybackView.Auto)
            PauseToPreview();
        else
        {
            if ((!_hasFloorSelection && !_inspectDecoration) || _rangeAnchor is not null)
                SelectFloor(0, true);
            SwitchPlaybackView(_shortcutAuto ? PlaybackView.Auto : PlaybackView.Manual, rate);
        }
    }

    private void ToggleShortcutAutoplay()
    {
        _shortcutAuto = !_shortcutAuto;
        if (_playbackView is PlaybackView.Auto or PlaybackView.Manual)
            SwitchPlaybackView(_shortcutAuto ? PlaybackView.Auto : PlaybackView.Manual, _playbackRate);
    }

    private void ToggleShortcutNoFail()
    {
        _shortcutNoFail = !_shortcutNoFail;
        if (_playback is { } renderer)
            renderer.PlaySession.NoFail = _shortcutNoFail;
    }
}
