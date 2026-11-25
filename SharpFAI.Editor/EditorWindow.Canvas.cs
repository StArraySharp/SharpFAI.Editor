using System.Globalization;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly record struct Direction(ImGuiKey Key, string Label, int Angle, int Fine = -1, bool Extra = false)
    {
        public int AngleFor(bool fine) => fine && Fine >= 0 ? Fine : Angle;
    }
    private static readonly Direction[] Directions = EditorCanvasInput.Directions.Select(binding =>
        new Direction(Enum.Parse<ImGuiKey>("ImGuiKey_" + binding.Key), binding.Key,
            binding.Angle, binding.Fine, binding.Extra)).ToArray();
    private bool FineDirections => ImGui.IsKeyDown(ImGuiKey.ImGuiKey_GraveAccent) != 0;

    private bool _directionGuide = true;
    private bool _showArbitraryAngle;
    private string? _angleDraft;
    private int? _centerEditFloor = 0;
    private int _pendingCameraFocusVersion = -1;
    private enum CanvasGesture { None, Click, Drag }
    private readonly record struct PendingTile(int Floor, int Version, double Direction);
    private readonly List<PendingTile> _pendingTiles = [];
    private readonly List<int> _canvasFloorHits = [];
    private readonly List<int> _decorationMarkers = [];
    private int _decorationMarkersVersion = -1;
    private readonly FloorHitCycle _floorHitCycle = new();
    private bool _canvasPress;
    private bool _canvasDrag;
    private double? _pressedDirection;
    private bool _pathLocked;
    private JObject? _centerEditDecoration;
    private JObject? _dragDecoration;
    private Vector2 _dragOffset;
    private EditorDocument? _trackGestureDocument;
    private int _trackFirst, _trackLast;
    private Vector2 _trackStart, _trackOffset;
    private bool _freeAngle;
    private bool _freeAngleClick;

    private void SelectFloor(int index, bool center)
    {
        int floor = Math.Clamp(index, 0, _document.LastFloor);
        if (_floor != floor)
            _eventPage = 0;
        _floor = floor;
        _hasFloorSelection = true;
        _rangeAnchor = null;
        _playbackFloor = floor;
        _centerEditFloor = center ? floor : null;
        _centerEditDecoration = null;
        _inspectDecoration = false;
        _decoration = null;
        _selectedDecorations.Clear();
    }

    private void InsertTile(double angle) => InsertTiles([angle]);

    private void InsertTiles(double[] angles)
    {
        if (_pathLocked || !_document.CanInsertTiles(_floor, angles))
            return;
        if (_pendingTiles.Count > 0 && _pendingTiles[^1].Floor != _floor)
            _pendingTiles.Clear();
        double heading = _document.IncomingAngle(_floor);
        _document.InsertTiles(_floor, angles);
        if (_playbackView == PlaybackView.Edit)
        {
            for (int i = 0; i < angles.Length; i++)
            {
                heading = angles[i] == 999 ? (heading + 180) % 360 : angles[i];
                _pendingTiles.Add(new(_floor + i + 1, _document.ChangeVersion, heading));
            }
        }
        SelectFloor(_floor + angles.Length, true);
    }

    private void DrawCanvas()
    {
        if (ImGui.Begin(CanvasWindow) != 0)
        {
            ApplySpeedWheel();
            if (_playbackView == PlaybackView.Edit)
                DrawEditCanvas();
            else
                DrawPlaybackCanvas();
        }
        ImGui.End();
    }

    private void SetCanvasOnly(bool enabled)
    {
        if (_canvasOnly == enabled) return;
        FinishNumericEdit();
        ImGui.ClearActiveID();
        ResetCanvasGesture();
        _dragDecoration = null;
        _playbackHovered = false;
        _canvasOnly = enabled;
    }

    private void DrawCanvasOnly()
    {
        ImGuiViewport* viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport->Pos, (int)ImGuiCond.ImGuiCond_Always, default);
        ImGui.SetNextWindowSize(viewport->Size, (int)ImGuiCond.ImGuiCond_Always);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowPadding, default(ImVec2));
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowBorderSize, 0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.ImGuiWindowFlags_NoDecoration |
            ImGuiWindowFlags.ImGuiWindowFlags_NoDocking | ImGuiWindowFlags.ImGuiWindowFlags_NoMove |
            ImGuiWindowFlags.ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags.ImGuiWindowFlags_NoBackground |
            ImGuiWindowFlags.ImGuiWindowFlags_NoScrollWithMouse;
        bool visible = true;
        if (ImGui.Begin("###chart-only", ref visible, (int)flags) != 0)
        {
            if (_playbackView == PlaybackView.Edit) DrawEditCanvas();
            else DrawPlaybackCanvas();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void UpdateDecorationMarkers()
    {
        if (_document.DecorationListVersion == _decorationMarkersVersion)
            return;

        _decorationMarkersVersion = _document.DecorationListVersion;
        _decorationMarkers.Clear();
        var decorations = _document.Decorations;
        for (int index = 0; index < decorations.Count; index++)
            if (decorations[index] is JObject item && EditorDocument.Type(item) is not ("AddDecoration" or "AddText"))
                _decorationMarkers.Add(index);
    }

    private static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label, null, 1).x + 2 * ImGui.GetStyle()->FramePadding.x;

    private static void ContinueToolbar(float width)
    {
        float right = ImGui.GetCursorScreenPos().x + ImGui.GetContentRegionAvail().x;
        if (ImGui.GetItemRectMax().x + ImGui.GetStyle()->ItemSpacing.x + width <= right)
            ImGui.SameLine();
    }

    private static bool ToolbarCheckbox(string key, ref bool value)
    {
        string label = L.T(key);
        ContinueToolbar(ImGui.CalcTextSize(label).x + ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x);
        return Toggle(label, ref value);
    }

    private bool DrawArbitraryAngleInput()
    {
        const string id = "##arbitrary-angle";
        bool submit = ImGui.GetCurrentContext()->ActiveId == ImGui.GetID(id) &&
            (Pressed(ImGuiKey.ImGuiKey_Enter) || Pressed(ImGuiKey.ImGuiKey_KeypadEnter));
        const int flags = (int)(ImGuiInputTextFlags.ImGuiInputTextFlags_AutoSelectAll |
            ImGuiInputTextFlags.ImGuiInputTextFlags_CharsScientific);
        string text = _angleDraft ?? _arbitraryAngle.ToString("R", CultureInfo.InvariantCulture);
        string? update = EditText(id, "", text, live: true, extraFlags: flags);
        bool valid = float.TryParse(update ?? text, NumberStyles.Float, CultureInfo.InvariantCulture, out float angle) &&
            float.IsFinite(angle);
        if (valid) _arbitraryAngle = angle;
        _angleDraft = ImGui.IsItemActive() != 0 ? update ?? text : null;
        return submit && valid;
    }

    private void DrawEditCanvasToolbar(bool ready)
    {
        string mode = L.T(_tilesOnly ? "TilesOnly" : "Preview") + "###edit-scene-mode";
        if (ImGui.Button(mode, default) != 0)
        {
            _tilesOnly = !_tilesOnly;
            ResetCanvasGesture();
            if (_tilesOnly && _inspectDecoration)
                SelectFloor(_floor, false);
        }
        WidgetDraw.ItemTooltip(L.T("EditSceneModeHelp"));
        ToolbarCheckbox("DirectionGuide", ref _directionGuide);
        ToolbarCheckbox("LockPath", ref _pathLocked);
        ToolbarCheckbox("Angle", ref _showArbitraryAngle);
        if (_showArbitraryAngle)
        {
            ContinueToolbar(80 * _uiScale);
            ImGui.SetNextItemWidth(80 * _uiScale);
            bool submitted = DrawArbitraryAngleInput();
            if (_hasFloorSelection)
            {
                ContinueToolbar(ButtonWidth(L.T("AddTile")));
                bool add = ImGui.Button(L.T("AddTile"), default) != 0;
                if (add || submitted)
                    InsertArbitraryAngle();
            }
        }
        ContinueToolbar(ButtonWidth(L.T("CenterSelection")));
        if (ImGui.Button(L.T("CenterSelection"), default) != 0)
        {
            if (_inspectDecoration && _decoration is not null)
                CenterOnDecoration(_decoration);
            else if (_hasFloorSelection)
            {
                _playbackFloor = _floor;
                _centerEditFloor = _floor;
            }
        }
        string status = ready ? $"{_playback!.Camera.Zoom:P0}" : _playback?.LoadError ??
            (_playback?.IsLoading == true ? L.F("SyncStage", L.LoadPhase(_playback.LoadPhase)) : L.T("Syncing"));
        ContinueToolbar(ImGui.CalcTextSize(status).x);
        ImGui.AlignTextToFramePadding();
        WidgetDraw.TextDisabled(status);
    }

    private void DrawEditCanvas()
    {
        bool ready = HasCurrentPlayback;
        if (!_canvasOnly) DrawEditCanvasToolbar(ready);
        _playbackHovered = false;
        ImVec2 size = ImGui.GetContentRegionAvail();
        size.x = Math.Max(1, size.x);
        size.y = Math.Max(1, size.y);
        LevelRenderer? renderer = _playback;
        bool canUseScene = renderer?.Level is not null && _displayedRevision?.Document == _document;
        var pending = canUseScene ? PendingTileCenters(renderer!) : [];
        bool previewingAngle = false;
        if (canUseScene && renderer is not null)
        {
            if (pending.Count <= 1) renderer.ClearEditorInsertedTrack();
            else if (renderer.EditorInsertedTrackNeedsUpdate(_document.ChangeVersion, _tilesOnly))
            {
                var inserted = new LevelRenderer.EditorInsertedFloor[_pendingTiles.Count];
                for (int i = 0; i < inserted.Length; i++)
                {
                    var tile = _pendingTiles[i];
                    inserted[i] = new(pending[i + 1], tile.Direction, _document.IncomingAngle(tile.Floor + 1),
                        _document.IsMidspinAt(tile.Floor));
                }
                renderer.SetEditorInsertedTrack(_document.ChangeVersion, _pendingTiles[0].Floor - 1, inserted, _tilesOnly);
            }
            if (pending.Count > 1 && _centerEditFloor == _pendingTiles[^1].Floor &&
                _pendingCameraFocusVersion != _pendingTiles[^1].Version)
            {
                CenterEditCamera(renderer, pending[^1]);
                _pendingCameraFocusVersion = _pendingTiles[^1].Version;
            }
            if (_centerEditDecoration is { } item)
            {
                CenterEditCamera(renderer, DecorationPosition(renderer, item));
                _centerEditDecoration = null;
            }
            previewingAngle = !_canvasOnly && UpdateFreeAngle(renderer, size, ready);
            RenderPlaybackFrame(renderer, size);
        }
        DrawPlaybackImage(size);
        if (!canUseScene || renderer is null)
        {
            ResetCanvasGesture();
            return;
        }
        if (previewingAngle) return;
        if (_canvasOnly)
        {
            _playbackHovered = ImGui.IsItemHovered() != 0;
            NavigatePlaybackPreview(renderer, selectTiles: false);
            return;
        }

        ImVec2 origin = ImGui.GetItemRectMin();
        ImVec2 end = ImGui.GetItemRectMax();
        ImGuiIO* io = ImGui.GetIO();
        Vector2 scale = new(_playbackWidth / (end.x - origin.x), _playbackHeight / (end.y - origin.y));
        Vector2 cursor = new((io->MousePos.x - origin.x) * scale.X, (io->MousePos.y - origin.y) * scale.Y);
        ImVec2 Screen(Vector2 world)
        {
            Vector2 pixel = renderer.Camera.WorldToScreen(world);
            return WidgetDraw.V2(origin.x + pixel.X / scale.X, origin.y + pixel.Y / scale.Y);
        }

        bool hovered = ImGui.IsItemHovered() != 0;
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImDrawList.PushClipRect(draw, origin, end, 1);
        float tileSize = renderer.TileSpacing;

        uint accent = WidgetDraw.U32(61, 223, 206);
        for (int i = 1; i < pending.Count; i++)
        {
            Vector2 from = pending[i - 1];
            Vector2 to = pending[i];
            Vector2 step = to - from;
            Vector2 normal = new Vector2(-step.Y, step.X) * 0.23f;
            ImVec2 a = Screen(from + normal);
            ImVec2 b = Screen(to + normal);
            ImVec2 c = Screen(to - normal);
            ImVec2 d = Screen(from - normal);
            ImDrawList.AddLine(draw, a, b, accent, 1.5f);
            ImDrawList.AddLine(draw, b, c, accent, 1.5f);
            ImDrawList.AddLine(draw, c, d, accent, 1.5f);
            ImDrawList.AddLine(draw, d, a, accent, 1.5f);
        }
        bool hasFloor = renderer.TryGetFloorCenter(_floor, out Vector2 selectedWorld);
        int pendingIndex = pending.Count > 1 ? _floor - _pendingTiles[0].Floor + 1 : -1;
        if (pendingIndex > 0 && pendingIndex < pending.Count)
        {
            selectedWorld = pending[pendingIndex];
            hasFloor = true;
        }
        ImVec2 selected = hasFloor ? Screen(selectedWorld) : default;
        bool selectionScene = ready || pending.Count > 1;
        bool guide = selectionScene && hasFloor && _hasFloorSelection && _directionGuide && !_inspectDecoration &&
            _rangeAnchor is null && !_showArbitraryAngle;
        if (selectionScene && _hasFloorSelection && !_inspectDecoration)
        {
            var (first, last) = FloorRange;
            // Pending tile indices may still refer to different tiles in the previous snapshot.
            int lastCommitted = _pendingTiles.Count == 0 ? last : Math.Min(last, _pendingTiles[0].Floor - 1);
            renderer.VisitFloorOutlines(first, Math.Min(lastCommitted, _floor - 1), DrawFloorOutline);
            renderer.VisitFloorOutlines(_floor + 1, lastCommitted, DrawFloorOutline);
            if (_floor <= lastCommitted)
                renderer.VisitFloorOutlines(_floor, _floor, DrawFloorOutline);
            if (hasFloor && _showBrickNumbers)
                ImDrawList.AddText(draw, WidgetDraw.V2(selected.x + 9, selected.y - 8), accent, _floor.ToString());
        }

        JObject? decorationHit = null;
        JObject? spriteHit = null;
        int spriteHitIndex = -1;
        Span<Vector2> sprite = stackalloc Vector2[4];
        bool hitSprites = hovered && (_inspectDecoration || io->KeyAlt != 0);
        if (!_tilesOnly && !_hideAllDecorations)
        {
            var decorations = _document.Decorations;
            if (ready && (hitSprites || _inspectDecoration))
            {
                foreach (int index in renderer.VisibleDecorationIndices)
                {
                    if ((uint)index >= (uint)decorations.Count || decorations[index] is not JObject item || DecorationHidden(item))
                        continue;
                    bool selectedDecoration = _inspectDecoration && _selectedDecorations.Contains(item);
                    bool canHit = hitSprites && index > spriteHitIndex && !DecorationLocked(item);
                    if ((!selectedDecoration && !canHit) || !renderer.TryGetDecorationQuad(index, sprite))
                        continue;
                    if (canHit && InsideQuad(sprite, cursor))
                    {
                        spriteHit = item;
                        spriteHitIndex = index;
                    }
                    if (selectedDecoration)
                    {
                        Vector2 offset = ReferenceEquals(item, _dragDecoration)
                            ? renderer.Camera.WorldToScreen(renderer.Camera.Position + _dragOffset) -
                              renderer.Camera.WorldToScreen(renderer.Camera.Position) : Vector2.Zero;
                        for (int edge = 0; edge < 4; edge++)
                            ImDrawList.AddLine(draw, SpriteCorner(sprite[edge] + offset),
                                SpriteCorner(sprite[(edge + 1) % 4] + offset), accent, 1.5f);
                    }
                }
            }

            UpdateDecorationMarkers();
            foreach (int index in _decorationMarkers)
            {
                var item = (JObject)decorations[index]!;
                if (DecorationHidden(item) || ready && renderer.TryGetDecorationQuad(index, sprite))
                    continue;
                Vector2 world = DecorationPosition(renderer, item);
                if (ReferenceEquals(item, _dragDecoration))
                    world += _dragOffset;
                ImVec2 point = Screen(world);
                if (point.x < origin.x - 12 || point.x > end.x + 12 || point.y < origin.y - 12 || point.y > end.y + 12)
                    continue;
                DrawDecorationMarker(draw, item, point, _inspectDecoration && _selectedDecorations.Contains(item));
                if (!DecorationLocked(item) && Math.Abs(io->MousePos.x - point.x) < 11 && Math.Abs(io->MousePos.y - point.y) < 11)
                    decorationHit = item;
            }
        }
        decorationHit ??= spriteHit;
        bool shift = io->KeyShift != 0;
        bool fine = FineDirections;
        double? direction = guide && !_pathLocked
            ? DrawDirectionGuide(draw, selected, origin, end, io->MousePos, shift, fine, hovered) : null;
        ImDrawList.PopClipRect(draw);

        const int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        bool leftPressed = hovered && ImGui.IsMouseClicked(left, 0) != 0;
        if (leftPressed)
        {
            // A guide button must not cover a stack of selectable floors.
            if (ready && direction is not null)
            {
                renderer.PickFloors(cursor, _canvasFloorHits);
                if (_canvasFloorHits.Count > 1 || (_canvasFloorHits.Count > 0 &&
                    (io->KeyAlt != 0 || shift && direction != -1)))
                    direction = null;
            }
            _pressedDirection = direction;
        }
        if (ready && leftPressed && direction is null && decorationHit is not null)
        {
            _floorHitCycle.Reset();
            ResetCanvasGesture();
            SelectDecoration(decorationHit);
            _dragDecoration = decorationHit;
            _dragOffset = Vector2.Zero;
        }

        Vector2 delta = new(io->MouseDelta.x * scale.X, io->MouseDelta.y * scale.Y);
        if (ready && hovered && !_pathLocked && !_inspectDecoration && _dragDecoration is null &&
            _trackGestureDocument is null && direction is null && decorationHit is null &&
            io->KeyAlt != 0 && leftPressed && renderer.PickFloor(cursor) is int hit && hit >= 0)
        {
            var range = FloorRange;
            if (!_hasFloorSelection || hit < range.First || hit > range.Last) SelectFloor(hit, false);
            (_trackFirst, _trackLast) = FloorRange;
            _trackStart = renderer.Camera.ScreenToWorld(cursor);
            _trackOffset = Vector2.Zero;
            ResetCanvasGesture();
            _trackGestureDocument = _document;
        }
        if (_trackGestureDocument is not null)
        {
            if (_pathLocked || !ReferenceEquals(_trackGestureDocument, _document) ||
                ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0)
            {
                _trackGestureDocument = null;
                return;
            }
            if (ImGui.IsMouseDragging(left, 3) != 0)
            {
                Vector2 offset = (renderer.Camera.ScreenToWorld(cursor) - _trackStart) / tileSize;
                _trackOffset = new Vector2((float)EditorCanvasInput.SnapPosition(offset.X, shift, io->KeyCtrl != 0),
                    (float)EditorCanvasInput.SnapPosition(offset.Y, shift, io->KeyCtrl != 0));
            }
            ImDrawList.PushClipRect(draw, origin, end, 1);
            for (int index = _trackFirst; index <= _trackLast; index++)
                if (renderer.TryGetFloorCenter(index, out Vector2 point))
                {
                    ImVec2 target = Screen(point + _trackOffset * tileSize);
                    ImDrawList.AddLine(draw, Screen(point), target, accent, 1);
                    ImDrawList.AddCircle(draw, target, 10 * _uiScale, accent, 20, 2);
                }
            ImDrawList.PopClipRect(draw);
            if (ImGui.IsMouseDown(left) == 0)
            {
                _pendingTiles.Clear();
                _document.PositionTiles(_trackFirst, _trackLast, _trackOffset.X, _trackOffset.Y);
                ResetCanvasGesture();
            }
            return;
        }
        if (_dragDecoration is { } dragged)
        {
            if (ImGui.IsMouseDragging(left, 3) != 0)
                _dragOffset += renderer.Camera.ScreenToWorld(delta) - renderer.Camera.ScreenToWorld(Vector2.Zero);
            if (ImGui.IsMouseDown(left) == 0)
            {
                if (_dragOffset.LengthSquared > 0.000001f)
                {
                    JArray? position = dragged["position"] as JArray;
                    _document.Set(dragged, "position", new JArray(
                        Component(position, 0) + _dragOffset.X / tileSize,
                        Component(position, 1) + _dragOffset.Y / tileSize));
                }
                _dragDecoration = null;
            }
        }
        else
        {
            if (hovered && io->MouseWheel != 0)
            {
                renderer.Zoom(io->MouseWheel, cursor);
            }
            switch (UpdateCanvasGesture(hovered))
            {
                case CanvasGesture.Drag:
                    if (_pressedDirection is null) renderer.Pan(delta);
                    break;
                case CanvasGesture.Click:
                    if (_pressedDirection is { } pressed)
                    {
                        if (direction == pressed)
                            InsertTile(pressed == -1 ? (_document.IncomingAngle(_floor) + 180) % 360 : pressed);
                    }
                    else if (ready && io->KeyAlt == 0)
                    {
                        renderer.PickFloors(cursor, _canvasFloorHits);
                        int floor = _floorHitCycle.Pick(_canvasFloorHits);
                        if (floor >= 0)
                        {
                            int? anchor = shift && _hasFloorSelection && !_inspectDecoration ? _rangeAnchor ?? _floor : null;
                            SelectFloor(floor, false);
                            if (anchor != floor)
                                _rangeAnchor = anchor;
                        }
                        else if (spriteHit is not null)
                            SelectDecoration(spriteHit);
                        else
                        {
                            _hasFloorSelection = _inspectDecoration = false;
                            _rangeAnchor = null;
                            _decoration = null;
                            _selectedDecorations.Clear();
                        }
                    }
                    break;
            }
        }

        void DrawFloorOutline(int index, ReadOnlySpan<Vector2> outline)
        {
            bool primary = index == _floor;
            uint color = primary ? accent : WidgetDraw.U32(116, 175, 255);
            float width = (primary ? 2.5f : 1.5f) * _uiScale;
            Stroke(outline, WidgetDraw.U32(13, 25, 37, 220), width + 2 * _uiScale);
            Stroke(outline, color, width);

            void Stroke(ReadOnlySpan<Vector2> points, uint strokeColor, float thickness)
            {
                ImVec2 from = SpriteCorner(points[^1]);
                foreach (Vector2 point in points)
                {
                    ImVec2 to = SpriteCorner(point);
                    ImDrawList.AddLine(draw, from, to, strokeColor, thickness);
                    from = to;
                }
            }
        }

        ImVec2 SpriteCorner(Vector2 position) => WidgetDraw.V2(origin.x + position.X / scale.X, origin.y + position.Y / scale.Y);
    }

    private static bool InsideQuad(ReadOnlySpan<Vector2> corners, Vector2 point)
    {
        Vector2 x = corners[1] - corners[0];
        Vector2 y = corners[3] - corners[0];
        Vector2 offset = point - corners[0];
        float determinant = x.X * y.Y - x.Y * y.X;
        if (Math.Abs(determinant) < 0.0001f)
            return false;
        float u = (offset.X * y.Y - offset.Y * y.X) / determinant;
        float v = (x.X * offset.Y - x.Y * offset.X) / determinant;
        return u is >= 0 and <= 1 && v is >= 0 and <= 1;
    }

    private List<Vector2> PendingTileCenters(LevelRenderer renderer)
    {
        if (_pendingTiles.Count == 0 || !renderer.TryGetFloorCenter(_pendingTiles[0].Floor - 1, out Vector2 position))
            return [];
        List<Vector2> centers = new(_pendingTiles.Count + 1) { position };
        foreach (PendingTile tile in _pendingTiles)
        {
            (float sin, float cos) = MathF.SinCos(MathHelper.DegreesToRadians((float)tile.Direction));
            position += new Vector2(cos, sin) * renderer.TileSpacing;
            centers.Add(position);
        }
        return centers;
    }

    private CanvasGesture UpdateCanvasGesture(bool hovered)
    {
        const int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        if (hovered && ImGui.IsMouseClicked(left, 0) != 0)
            _canvasPress = true;
        if (!_canvasPress)
            return CanvasGesture.None;
        if (ImGui.IsMouseDown(left) == 0)
        {
            bool click = !_canvasDrag && hovered && ImGui.IsMouseReleased(left) != 0;
            ResetCanvasGesture();
            return click ? CanvasGesture.Click : CanvasGesture.None;
        }
        _canvasDrag |= ImGui.IsMouseDragging(left, 4) != 0;
        return _canvasDrag ? CanvasGesture.Drag : CanvasGesture.None;
    }

    private void ResetCanvasGesture()
    {
        _canvasPress = _canvasDrag = _playbackPan = false;
        _trackGestureDocument = null;
        _freeAngle = _freeAngleClick = false;
        _playback?.ClearEditorAnglePreview();
    }

    private bool UpdateFreeAngle(LevelRenderer renderer, ImVec2 size, bool ready)
    {
        const int right = (int)ImGuiMouseButton.ImGuiMouseButton_Right;
        const int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        ImGuiIO* io = ImGui.GetIO();
        ImVec2 origin = ImGui.GetCursorScreenPos();
        Vector2 pointer = new(io->MousePos.x - origin.x, io->MousePos.y - origin.y);
        bool hovered = pointer.X >= 0 && pointer.X < size.x && pointer.Y >= 0 && pointer.Y < size.y &&
            ImGui.IsWindowHovered(0) != 0;
        bool allowed = !_pathLocked && _hasFloorSelection && _rangeAnchor is null && !_inspectDecoration &&
            _dragDecoration is null && _trackGestureDocument is null && !ShortcutContextBlocked;
        if (!_freeAngle)
        {
            if (!ready || !allowed || !hovered || ImGui.IsMouseClicked(right, 0) == 0) return false;
            ResetCanvasGesture();
            _freeAngle = true;
        }
        if (!allowed || ImGui.IsMouseDown(right) == 0 || Pressed(ImGuiKey.ImGuiKey_Escape))
        {
            ResetCanvasGesture();
            return false;
        }
        if (!ready || !renderer.TryGetFloorCenter(_floor, out Vector2 pivot))
        {
            renderer.ClearEditorAnglePreview();
            _freeAngleClick = false;
            return true;
        }

        Vector2 pixel = pointer * new Vector2(renderer.Camera.ViewportWidth / size.x, renderer.Camera.ViewportHeight / size.y);
        if (hovered && io->MouseWheel != 0) renderer.Zoom(io->MouseWheel, pixel);
        Vector2 vector = renderer.Camera.ScreenToWorld(pixel) - pivot;
        double angle = vector.LengthSquared > .000001f
            ? EditorCanvasInput.Rotation(vector.X, vector.Y, io->KeyShift != 0)
            : 90;
        if (ImGui.IsMouseClicked(left, 0) != 0) _freeAngleClick = hovered;
        if (_freeAngleClick && ImGui.IsMouseReleased(left) != 0)
        {
            _freeAngleClick = false;
            if (hovered)
            {
                int version = _document.ChangeVersion;
                if (_document.IsFreeAngleBacktrack(_floor, angle))
                    DeleteFloorRange(_floor, _floor, _floor - 1);
                else
                    InsertTile(angle);
                if (version != _document.ChangeVersion) _centerEditFloor = _floor;
            }
            renderer.ClearEditorAnglePreview();
        }
        else
            renderer.SetEditorAnglePreview(_floor, angle);
        return true;
    }

    private void SeekEditFloor(LevelRenderer renderer, int floor)
    {
        var previous = _savedEditCamera ?? (renderer.Camera.Position, renderer.Camera.Zoom);
        _savedEditCamera = null;
        renderer.SelectFloor(floor);
        if (_centerEditFloor is int focus && renderer.TryGetFloorCenter(focus, out Vector2 center))
            previous.Position = center;
        RestoreEditCamera(renderer, previous);
        _centerEditFloor = null;
    }

    private static void RestoreEditCamera(LevelRenderer renderer, (Vector2 Position, float Zoom) view)
    {
        renderer.Camera.Zoom = view.Zoom;
        renderer.Camera.Rotation = 0;
        CenterEditCamera(renderer, view.Position);
    }

    private static void CenterEditCamera(LevelRenderer renderer, Vector2 position)
    {
        renderer.Pan(Vector2.Zero);
        renderer.Camera.Position = position;
        renderer.Camera.MarkDirty();
    }

    private double? DrawDirectionGuide(ImDrawList* draw, ImVec2 floor, ImVec2 origin, ImVec2 end, ImVec2 cursor,
        bool shift, bool fine, bool hovered)
    {
        float half = 17 * _uiScale;
        float radius = Math.Max(0, Math.Min((shift ? 89 : 76) * _uiScale,
            Math.Min(end.x - origin.x, end.y - origin.y) * .5f - half - 2));
        ImVec2 center = floor;
        double? result = null;
        float nearest = float.PositiveInfinity;
        foreach (var direction in Directions)
        {
            bool horizontal = direction.Angle is 0 or 180;
            if (!EditorCanvasInput.VisibleDirection(direction.Extra, horizontal, shift, fine))
                continue;
            double angle = direction.AngleFor(fine);
            double radians = angle * Math.PI / 180;
            Button(WidgetDraw.V2(center.x + (float)Math.Cos(radians) * radius,
                center.y - (float)Math.Sin(radians) * radius), direction.Label, direction.Key, angle);
        }
        if (shift)
            Button(center, "Space", ImGuiKey.ImGuiKey_Space, -1);
        else
        {
            float corner = radius + half * 0.75f;
            ImVec2 hint = WidgetDraw.V2(center.x - corner, center.y - corner);
            ImVec2 min = WidgetDraw.V2(hint.x - half, hint.y - half * 0.65f);
            ImVec2 max = WidgetDraw.V2(hint.x + half, hint.y + half * 0.65f);
            WidgetDraw.Panel(draw, min, max, WidgetDraw.U32(31, 34, 49, 208), 4);
            WidgetDraw.TextCentered(draw, min, max, WidgetDraw.U32(183, 187, 202), "Tab");
        }
        return result;

        void Button(ImVec2 point, string label, ImGuiKey key, double angle)
        {
            ImVec2 min = WidgetDraw.V2(point.x - half, point.y - half);
            ImVec2 max = WidgetDraw.V2(point.x + half, point.y + half);
            float x = cursor.x - point.x, y = cursor.y - point.y;
            bool hot = hovered && Math.Abs(x) < half && Math.Abs(y) < half;
            if (hot && x * x + y * y < nearest)
            {
                nearest = x * x + y * y;
                result = angle;
            }
            uint color = hot || ImGui.IsKeyDown(key) != 0 ? WidgetDraw.U32(29, 117, 117) : WidgetDraw.U32(31, 34, 49, 240);
            WidgetDraw.Panel(draw, min, max, color, 4);
            WidgetDraw.Outline(draw, min, max, WidgetDraw.U32(111, 119, 140), 4);
            WidgetDraw.TextCentered(draw, min, max, WidgetDraw.U32(242, 243, 248), label);
        }
    }

    private void DrawDecorationMarker(ImDrawList* draw, JObject item, ImVec2 point, bool selected)
    {
        ImVec2 min = WidgetDraw.V2(point.x - 9, point.y - 9);
        ImVec2 max = WidgetDraw.V2(point.x + 9, point.y + 9);
        uint accent = selected ? WidgetDraw.U32(70, 235, 220) : WidgetDraw.U32(186, 145, 225);
        WidgetDraw.Panel(draw, min, max, WidgetDraw.U32(35, 31, 51), 4);
        WidgetDraw.Outline(draw, min, max, accent, 4);
        ImDrawList.AddCircleFilled(draw, point, 3, accent);
        if (selected)
            ImDrawList.AddText(draw, WidgetDraw.V2(point.x + 12, point.y - 8), accent, DecorationName(item));
    }

    private static Vector2 DecorationPosition(LevelRenderer renderer, JObject item)
    {
        JArray? position = item["position"] as JArray;
        Vector2 anchor = item.Value<string>("relativeTo") == "Tile" &&
                         renderer.TryGetFloorCenter(Math.Clamp(EditorDocument.Floor(item), 0, renderer.FloorCount - 1), out Vector2 center)
            ? center : Vector2.Zero;
        return anchor + new Vector2((float)Component(position, 0), (float)Component(position, 1)) * renderer.TileSpacing;
    }

    private void CenterOnDecoration(JObject item)
    {
        if (_playbackView != PlaybackView.Edit)
            SwitchPlaybackView(PlaybackView.Edit);
        _centerEditDecoration = item;
    }
}
