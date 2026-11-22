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

    private bool _grid = true;
    private bool _directionGuide = true;
    private bool _showArbitraryAngle;
    private bool _centerEditSelection = true;
    private enum CanvasGesture { None, Click, Drag }
    private readonly record struct PendingTile(int Floor, int Version, double Direction);
    private readonly List<PendingTile> _pendingTiles = [];
    private bool _centerPendingTile;
    private bool _canvasPress;
    private bool _canvasDrag;
    private double? _pressedDirection;
    private bool _pathLocked;
    private JObject? _centerEditDecoration;
    private JObject? _dragDecoration;
    private Vector2 _dragOffset;
    private EditorDocument? _trackGestureDocument;
    private bool _rotatingTrack;
    private int _trackFirst, _trackLast;
    private Vector2 _trackStart, _trackOffset;
    private double? _trackAngle;

    private void SelectFloor(int index, bool center)
    {
        int floor = Math.Clamp(index, 0, _document.LastFloor);
        if (_floor != floor)
            _eventPage = 0;
        _floor = floor;
        _hasFloorSelection = true;
        _rangeAnchor = null;
        _playbackFloor = floor;
        _centerEditSelection = center;
        _centerEditDecoration = null;
        _inspectDecoration = false;
        _decoration = null;
        _selectedDecorations.Clear();
    }

    private void InsertTile(double angle) => InsertTiles([angle]);

    private void InsertTiles(double[] angles)
    {
        if (_pathLocked)
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
            _centerPendingTile = true;
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

    private static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label, null, 1).x + 2 * ImGui.GetStyle()->FramePadding.x;

    private static void ContinueToolbar(float width)
    {
        float right = ImGui.GetCursorScreenPos().x + ImGui.GetContentRegionAvail().x;
        if (ImGui.GetItemRectMax().x + ImGui.GetStyle()->ItemSpacing.x + width <= right)
            ImGui.SameLine();
    }

    private void EditViewCheckbox(string key, ref bool value)
    {
        string label = L.T(key);
        ContinueToolbar(ImGui.CalcTextSize(label).x + ImGui.GetFrameHeight() + ImGui.GetStyle()->ItemInnerSpacing.x);
        Toggle(label, ref value);
    }

    private void DrawEditCanvas()
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
        EditViewCheckbox("Grid", ref _grid);
        EditViewCheckbox("DirectionGuide", ref _directionGuide);
        EditViewCheckbox("LockPath", ref _pathLocked);
        EditViewCheckbox("Angle", ref _showArbitraryAngle);
        if (_showArbitraryAngle)
        {
            ContinueToolbar(80 * _uiScale);
            ImGui.SetNextItemWidth(80 * _uiScale);
            float angle = _arbitraryAngle;
            bool submitted = ImGui.InputFloat("##arbitrary-angle", ref angle, 0, 0, "%.4g",
                (int)ImGuiInputTextFlags.ImGuiInputTextFlags_EnterReturnsTrue) != 0;
            if (submitted)
                _arbitraryAngle = angle;
            if (_hasFloorSelection)
            {
                ContinueToolbar(ButtonWidth(L.T("AddTile")));
                if (ImGui.Button(L.T("AddTile"), default) != 0 || submitted)
                    InsertArbitraryAngle();
            }
        }
        ContinueToolbar(ButtonWidth(L.T("CenterSelection")));
        if (ImGui.Button(L.T("CenterSelection"), default) != 0)
        {
            if (_inspectDecoration && _decoration is not null)
                CenterOnDecoration(_decoration);
            else
                SelectFloor(_floor, true);
        }
        bool ready = HasCurrentPlayback;
        string status = ready ? $"{_playback!.Camera.Zoom:P0}" : _playback?.LoadError ??
            (_playback?.IsLoading == true ? L.F("SyncStage", L.LoadPhase(_playback.LoadPhase)) : L.T("Syncing"));
        ContinueToolbar(ImGui.CalcTextSize(status).x);
        ImGui.AlignTextToFramePadding();
        WidgetDraw.TextDisabled(status);

        ImVec2 size = ImGui.GetContentRegionAvail();
        size.x = Math.Max(1, size.x);
        size.y = Math.Max(1, size.y);
        LevelRenderer? renderer = _playback;
        bool canUseScene = renderer?.Level is not null && _displayedRevision?.Document == _document;
        var pending = canUseScene ? PendingTileCenters(renderer!) : [];
        if (canUseScene && renderer is not null)
        {
            if (_centerPendingTile && pending.Count > 1 && _pendingTiles[^1].Floor == _floor)
                CenterEditCamera(renderer, pending[^1]);
            _centerPendingTile = false;
            if (_centerEditDecoration is { } item)
            {
                CenterEditCamera(renderer, DecorationPosition(renderer, item));
                _centerEditDecoration = null;
            }
            RenderPlaybackFrame(renderer, size);
        }
        DrawPlaybackImage(size);
        if (!canUseScene || renderer is null)
        {
            ResetCanvasGesture();
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
        if (_grid)
            DrawGrid(draw, origin, end, Screen(Vector2.Zero), Math.Max(23, tileSize * renderer.Camera.Zoom / scale.X));

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
        bool guide = hasFloor && _hasFloorSelection && _directionGuide && !_inspectDecoration && _rangeAnchor is null && !_showArbitraryAngle;
        if (hasFloor && _hasFloorSelection && !_inspectDecoration)
        {
            float radius = Math.Clamp(tileSize * renderer.Camera.Zoom / (4 * scale.X), 6, 25);
            ImDrawList.AddCircle(draw, selected, radius, accent, 20, 2);
            if (_showBrickNumbers)
                ImDrawList.AddText(draw, WidgetDraw.V2(selected.x + radius + 3, selected.y - 8), accent, _floor.ToString());
            if (_rangeAnchor is int anchor && renderer.TryGetFloorCenter(anchor, out Vector2 anchorPosition))
                ImDrawList.AddCircle(draw, Screen(anchorPosition), radius, accent, 20, 2);
        }

        JObject? decorationHit = null;
        JObject? spriteHit = null;
        Span<Vector2> sprite = stackalloc Vector2[4];
        for (int index = 0; !_tilesOnly && index < _document.Decorations.Count; index++)
        {
            if (_document.Decorations[index] is not JObject item || DecorationHidden(item))
                continue;
            bool selectedDecoration = _inspectDecoration && _selectedDecorations.Contains(item);
            if (EditorDocument.Type(item) is "AddDecoration" or "AddText")
            {
                if (!ready || !renderer.TryGetDecorationQuad(index, sprite))
                    continue;
                if (!DecorationLocked(item) && InsideQuad(sprite, cursor))
                    spriteHit = item;
                if (selectedDecoration)
                {
                    Vector2 offset = ReferenceEquals(item, _dragDecoration)
                        ? renderer.Camera.WorldToScreen(renderer.Camera.Position + _dragOffset) -
                          renderer.Camera.WorldToScreen(renderer.Camera.Position) : Vector2.Zero;
                    for (int edge = 0; edge < 4; edge++)
                        ImDrawList.AddLine(draw, SpriteCorner(sprite[edge] + offset),
                            SpriteCorner(sprite[(edge + 1) % 4] + offset), accent, 1.5f);
                }
                continue;
            }
            Vector2 world = DecorationPosition(renderer, item);
            if (ReferenceEquals(item, _dragDecoration))
                world += _dragOffset;
            ImVec2 point = Screen(world);
            if (point.x < origin.x - 12 || point.x > end.x + 12 || point.y < origin.y - 12 || point.y > end.y + 12)
                continue;
            DrawDecorationMarker(draw, item, point, selectedDecoration);
            if (!DecorationLocked(item) && Math.Abs(io->MousePos.x - point.x) < 11 && Math.Abs(io->MousePos.y - point.y) < 11)
                decorationHit = item;
        }
        if (_inspectDecoration || io->KeyAlt != 0)
            decorationHit ??= spriteHit;
        bool shift = io->KeyShift != 0;
        bool fine = FineDirections;
        double? direction = guide && !_pathLocked
            ? DrawDirectionGuide(draw, selected, origin, end, io->MousePos, shift, fine, hovered) : null;
        ImDrawList.PopClipRect(draw);

        const int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        if (hovered && ImGui.IsMouseClicked(left, 0) != 0)
            _pressedDirection = direction;
        if (ready && hovered && direction is null && decorationHit is not null && ImGui.IsMouseClicked(left, 0) != 0)
        {
            ResetCanvasGesture();
            SelectDecoration(decorationHit);
            _dragDecoration = decorationHit;
            _dragOffset = Vector2.Zero;
        }

        Vector2 delta = new(io->MouseDelta.x * scale.X, io->MouseDelta.y * scale.Y);
        const int right = (int)ImGuiMouseButton.ImGuiMouseButton_Right;
        if (ready && hovered && !_pathLocked && !_inspectDecoration && _dragDecoration is null &&
            _trackGestureDocument is null && direction is null && decorationHit is null)
        {
            if (io->KeyAlt != 0 && ImGui.IsMouseClicked(left, 0) != 0 && renderer.PickFloor(cursor) is int hit && hit >= 0)
            {
                var range = FloorRange;
                if (!_hasFloorSelection || hit < range.First || hit > range.Last) SelectFloor(hit, false);
                (_trackFirst, _trackLast) = FloorRange;
                _trackStart = renderer.Camera.ScreenToWorld(cursor);
                _trackOffset = Vector2.Zero;
                _rotatingTrack = false;
                ResetCanvasGesture();
                _trackGestureDocument = _document;
            }
            else if (_hasFloorSelection && _rangeAnchor is null && _document.CanRotateExit(_floor) &&
                ImGui.IsMouseClicked(right, 0) != 0)
            {
                _trackFirst = _trackLast = _floor;
                _trackAngle = null;
                _rotatingTrack = true;
                ResetCanvasGesture();
                _trackGestureDocument = _document;
            }
        }
        if (_trackGestureDocument is not null)
        {
            if (_pathLocked || !ReferenceEquals(_trackGestureDocument, _document) ||
                ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0)
            {
                _trackGestureDocument = null;
                return;
            }
            int button = _rotatingTrack ? right : left;
            if (_rotatingTrack && renderer.TryGetFloorCenter(_trackFirst, out Vector2 pivot))
            {
                Vector2 vector = renderer.Camera.ScreenToWorld(cursor) - pivot;
                if (vector.LengthSquared > .000001f)
                    _trackAngle = EditorCanvasInput.Rotation(vector.X, vector.Y, shift);
                if (_trackAngle is double angle)
                {
                    double radians = angle * Math.PI / 180;
                    Vector2 destination = pivot + new Vector2((float)Math.Cos(radians), (float)Math.Sin(radians)) * tileSize;
                    ImDrawList.PushClipRect(draw, origin, end, 1);
                    ImDrawList.AddLine(draw, Screen(pivot), Screen(destination), accent, 3);
                    ImDrawList.AddCircle(draw, Screen(destination), 10 * _uiScale, accent, 20, 2);
                    ImDrawList.PopClipRect(draw);
                }
            }
            else if (!_rotatingTrack && ImGui.IsMouseDragging(left, 3) != 0)
            {
                Vector2 offset = (renderer.Camera.ScreenToWorld(cursor) - _trackStart) / tileSize;
                _trackOffset = new Vector2((float)EditorCanvasInput.SnapPosition(offset.X, shift, io->KeyCtrl != 0),
                    (float)EditorCanvasInput.SnapPosition(offset.Y, shift, io->KeyCtrl != 0));
            }
            if (!_rotatingTrack)
            {
                ImDrawList.PushClipRect(draw, origin, end, 1);
                for (int index = _trackFirst; index <= _trackLast; index++)
                    if (renderer.TryGetFloorCenter(index, out Vector2 point))
                    {
                        ImVec2 target = Screen(point + _trackOffset * tileSize);
                        ImDrawList.AddLine(draw, Screen(point), target, accent, 1);
                        ImDrawList.AddCircle(draw, target, 10 * _uiScale, accent, 20, 2);
                    }
                ImDrawList.PopClipRect(draw);
            }
            if (ImGui.IsMouseDown(button) == 0)
            {
                _pendingTiles.Clear();
                if (_rotatingTrack && _trackAngle is double angle) _document.RotateExit(_trackFirst, angle);
                else if (!_rotatingTrack) _document.PositionTiles(_trackFirst, _trackLast, _trackOffset.X, _trackOffset.Y);
                _trackGestureDocument = null;
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
                    else if (direction is not null)
                        break;
                    else if (ready && renderer.PickFloor(cursor) is int floor && floor >= 0)
                    {
                        int anchor = _rangeAnchor ?? _floor;
                        SelectFloor(floor, false);
                        if (io->KeyShift != 0 && anchor != _floor)
                            _rangeAnchor = anchor;
                    }
                    else if (spriteHit is not null)
                        SelectDecoration(spriteHit);
                    break;
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
        _canvasPress = _canvasDrag = false;
        _trackGestureDocument = null;
    }

    private void SeekEditFloor(LevelRenderer renderer, int floor)
    {
        var previous = _savedEditCamera ?? (renderer.Camera.Position, renderer.Camera.Zoom);
        _savedEditCamera = null;
        renderer.SelectFloor(floor);
        renderer.Camera.Zoom = previous.Zoom;
        renderer.Camera.Rotation = 0;
        CenterEditCamera(renderer, _centerEditSelection && renderer.TryGetFloorCenter(floor, out Vector2 center)
            ? center : previous.Position);
        _centerEditSelection = false;
    }

    private static void CenterEditCamera(LevelRenderer renderer, Vector2 position)
    {
        renderer.Pan(Vector2.Zero);
        renderer.Camera.Position = position;
        renderer.Camera.MarkDirty();
    }

    private static void DrawGrid(ImDrawList* draw, ImVec2 origin, ImVec2 end, ImVec2 anchor, float spacing)
    {
        float x0 = origin.x + ((anchor.x - origin.x) % spacing + spacing) % spacing;
        float y0 = origin.y + ((anchor.y - origin.y) % spacing + spacing) % spacing;
        uint color = WidgetDraw.U32(45, 47, 62);
        for (float y = y0; y < end.y; y += spacing)
            for (float x = x0; x < end.x; x += spacing)
                ImDrawList.AddRectFilled(draw, WidgetDraw.V2(x, y), WidgetDraw.V2(x + 1, y + 1), color);
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
