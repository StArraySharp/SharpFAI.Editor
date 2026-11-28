using ImGui4NET;
using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    // Keep the prototype out of the editor until its controls and rendering are ready.
    private static readonly bool SceneViewAvailable = false;
    private const int SceneTileLimit = 4096;
    private static uint SceneAccent => WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_CheckMark);
    private static readonly uint[] SceneAxisColors =
        [WidgetDraw.U32(236, 108, 118), WidgetDraw.U32(123, 207, 142), WidgetDraw.U32(106, 163, 244)];
    private static readonly string[] SceneAxisLabels = ["X", "Y", "Z"];
    private static readonly Vector3[] SceneAxes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];

    private readonly SceneViewCamera _sceneCamera = new();
    private readonly List<int> _sceneCandidates = [];
    private readonly List<SceneDrawTile> _sceneTiles = [];
    private readonly Dictionary<OfficialFloorMeshBaker.Shape, System.Numerics.Vector2[]> _sceneShapes = [];
    private bool _showSceneView;
    private bool _sceneFocus;
    private bool _sceneFollowPlayback = true;
    private bool _sceneHovered;
    private bool _sceneSelectPress;
    private int _sceneDragButton = -1;
    private bool _scenePan;
    private bool _sceneDolly;

    private readonly record struct SceneDrawTile(int Index, LevelRenderer.SceneTile Tile, Vector3 Center, float Depth);

    private void SetSceneView(bool enabled)
    {
        if (enabled && !SceneViewAvailable) return;
        if (_showSceneView == enabled) return;
        ReleasePlaybackInput();
        _showSceneView = enabled;
        _sceneFocus = enabled;
        ResetCanvasGesture();
    }

    private void ResetSceneGesture()
    {
        _sceneSelectPress = _sceneHovered = false;
        _sceneDragButton = -1;
    }

    private void DrawSceneView(bool editing = true)
    {
        bool ready = HasCurrentPlayback && _playback is not null;
        if (!_canvasOnly)
        {
            if (editing)
            {
                if (ImGui.Button("2D##scene-view", default) != 0)
                {
                    SetSceneView(false);
                    return;
                }
                WidgetDraw.ItemTooltip(L.T("SceneView2D"));
            }
            string projectionLabel = L.T(_sceneCamera.Orthographic ? "SceneOrthographic" : "ScenePerspective");
            if (editing) ContinueToolbar(ButtonWidth(projectionLabel));
            if (ImGui.Button(projectionLabel, default) != 0)
                _sceneCamera.Orthographic = !_sceneCamera.Orthographic;
            WidgetDraw.ItemTooltip(L.T("SceneProjectionHelp"));
            if (editing)
            {
                ContinueToolbar(EditorControls.IconButtonWidth);
                if (EditorControls.Action("scene-focus", EditorControls.ControlIcon.Center)) _sceneFocus = true;
                WidgetDraw.ItemTooltip(L.T("CenterSelection") + " · F");
            }
            else
                ToolbarToggle("SceneFollowPlanets", ref _sceneFollowPlayback);
            ToolbarReadout("?");
            WidgetDraw.ItemTooltip(L.T(editing ? "SceneNavigationHelp" : "ScenePlaybackNavigationHelp"));
        }

        ImVec2 available = ImGui.GetContentRegionAvail();
        var size = new Vector2(Math.Max(1, available.x), Math.Max(1, available.y));
        ImGui.InvisibleButton("##scene-viewport", WidgetDraw.V2(size.X, size.Y));
        ImVec2 first = ImGui.GetItemRectMin(), last = ImGui.GetItemRectMax();
        Vector2 origin = new(first.x, first.y);
        bool hovered = _sceneHovered = ImGui.IsItemHovered() != 0;
        _playbackHovered = !editing && hovered;
        ImGuiIO* io = ImGui.GetIO();
        Vector2 cursor = new(io->MousePos.x - origin.X, io->MousePos.y - origin.Y);
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImDrawList.PushClipRect(draw, first, last, 1);

        LevelRenderer? renderer = _playback;
        if (!ready || renderer is null)
        {
            ImDrawList.AddRectFilled(draw, first, last, WidgetDraw.U32(23, 30, 39));
            string status = renderer?.LoadError ?? (renderer?.IsLoading == true
                ? L.F("SyncStage", L.LoadPhase(renderer.LoadPhase)) : L.T("Syncing"));
            WidgetDraw.TextCentered(draw, first, last, WidgetDraw.U32(179, 192, 208), status);
            ImDrawList.PopClipRect(draw);
            return;
        }

        float spacing = renderer.TileSpacing;
        if (!editing && (_sceneFollowPlayback || _sceneFocus))
        {
            Vector2 target = renderer.ScenePlaybackTarget;
            _sceneCamera.Focus(new Vector3d(target.X, target.Y, 0), _sceneFocus);
            _sceneFocus = false;
        }
        else if (editing && (_sceneFocus || _centerEditDecoration is not null))
        {
            JObject? focusDecoration = _centerEditDecoration ?? (_inspectDecoration ? _decoration : null);
            if (focusDecoration is not null)
                _sceneCamera.Focus(SceneDecorationPosition(renderer, focusDecoration), _sceneFocus);
            else if (renderer.TryGetSceneTile(_floor, out var focus))
                _sceneCamera.Focus(new Vector3d(focus.Center.X, focus.Center.Y, 0), _sceneFocus);
            _centerEditDecoration = null;
            _sceneFocus = false;
        }

        var projection = _sceneCamera.CreateProjection(size, spacing);
        Vector2 axisCenter = new(size.X - 52 * _uiScale, 54 * _uiScale);
        bool overAxes = !_canvasOnly && size.X >= 120 && size.Y >= 110 &&
                        Vector2.DistanceSquared(cursor, axisCenter) < MathF.Pow(51 * _uiScale, 2);
        bool pick = HandleSceneNavigation(hovered, overAxes, size, spacing, io, editing) && editing && !_canvasOnly;
        if (overAxes && hovered && ImGui.IsMouseClicked(0, 0) != 0 &&
            FindSceneAxis(projection, axisCenter, cursor) is int axis)
            _sceneCamera.SnapAxis(axis);
        projection = _sceneCamera.CreateProjection(size, spacing);

        var renderView = _sceneCamera.CreateRenderView(size, spacing);
        RenderPlaybackFrame(renderer, WidgetDraw.V2(size.X, size.Y), renderView);
        ImDrawList.AddImage(draw, new ImTextureRef { _TexID = (uint)_playbackTexture }, first, last,
            WidgetDraw.V2(0, 1), WidgetDraw.V2(1, 0), uint.MaxValue);
        if (!_canvasOnly) _modUi?.DrawOverlay(draw, first, last, renderer, scene3D: true);
        if (editing && !_canvasOnly)
        {
            DrawSceneGrid(draw, origin, projection, spacing);
            int floorHit = DrawSceneTiles(draw, origin, projection, renderer, renderView.Coverage, cursor, pick);
            JObject? decorationHit = DrawSceneDecorations(draw, origin, renderer, cursor, pick && _inspectDecoration, io);
            if (decorationHit is not null && (io->KeyShift == 0 || floorHit < 0))
                SelectDecoration(decorationHit);
            else if (floorHit >= 0)
            {
                int? anchor = io->KeyShift != 0 && _hasFloorSelection && !_inspectDecoration ? _rangeAnchor ?? _floor : null;
                SelectFloor(floorHit, false);
                if (anchor != floorHit) _rangeAnchor = anchor;
            }
        }
        if (!_canvasOnly && size.X >= 120 && size.Y >= 110)
            DrawSceneAxes(draw, origin, projection, axisCenter);
        ImDrawList.PopClipRect(draw);
        if (!_canvasOnly) DrawSpeedIndicator(first, last);
    }

    private bool HandleSceneNavigation(bool hovered, bool overAxes, Vector2 size, float spacing, ImGuiIO* io, bool editing)
    {
        const int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        const int right = (int)ImGuiMouseButton.ImGuiMouseButton_Right;
        const int middle = (int)ImGuiMouseButton.ImGuiMouseButton_Middle;
        if (_sceneDragButton >= 0 && ImGui.IsMouseDown(_sceneDragButton) == 0)
            _sceneDragButton = -1;
        if (hovered && !overAxes)
        {
            if (io->MouseWheel != 0) _sceneCamera.Zoom(io->MouseWheel);
            if (ImGui.IsMouseClicked(middle, 0) != 0) Start(middle, pan: true, dolly: false);
            else if (_playbackView != PlaybackView.Manual && ImGui.IsMouseClicked(right, 0) != 0)
                Start(right, pan: false, dolly: io->KeyAlt != 0);
            else if (ImGui.IsMouseClicked(left, 0) != 0)
                _sceneSelectPress = true;
        }

        if (_sceneSelectPress && ImGui.IsMouseDragging(left, 4) != 0)
            Start(left, pan: false, dolly: false);
        if (_sceneDragButton >= 0)
        {
            Vector2 delta = new(io->MouseDelta.x, io->MouseDelta.y);
            if (_scenePan && delta != Vector2.Zero)
            {
                if (!editing) _sceneFollowPlayback = false;
                _sceneCamera.Pan(delta, size.Y, spacing);
            }
            else if (_sceneDolly) _sceneCamera.Zoom((delta.X - delta.Y) / 24f);
            else if (!_scenePan) _sceneCamera.Orbit(delta);
        }

        if (ImGui.IsMouseReleased(left) == 0) return false;
        bool pick = _sceneSelectPress && hovered && !overAxes;
        _sceneSelectPress = false;
        return pick;

        void Start(int button, bool pan, bool dolly)
        {
            _sceneDragButton = button;
            _scenePan = pan;
            _sceneDolly = dolly;
            _sceneSelectPress = false;
        }
    }

    private void DrawSceneGrid(ImDrawList* draw, Vector2 origin, SceneViewCamera.Projection view, float tile)
    {
        Vector3d pivot = _sceneCamera.Pivot;
        float stride = tile;
        while (stride * view.Scale < 22 * _uiScale) stride *= 5;
        double baseX = Math.Floor(pivot.X / stride);
        double baseY = Math.Floor(pivot.Y / stride);
        float localX = (float)(baseX * stride - pivot.X);
        float localY = (float)(baseY * stride - pivot.Y);
        float z = (float)-pivot.Z - tile * .09f;
        const int lines = 24;
        float span = stride * lines;
        for (int i = -lines; i <= lines; i++)
        {
            float x = localX + stride * i, y = localY + stride * i;
            DrawSceneLine(draw, origin, view, new(x, localY - span, z), new(x, localY + span, z),
                GridColor(baseX + i, SceneAxisColors[1]));
            DrawSceneLine(draw, origin, view, new(localX - span, y, z), new(localX + span, y, z),
                GridColor(baseY + i, SceneAxisColors[0]));
        }

        static uint GridColor(double index, uint axisColor) => index == 0 ? (axisColor & 0x00FFFFFFu) | 0x48000000u
            : index % 5 == 0 ? WidgetDraw.U32(99, 118, 140, 65) : WidgetDraw.U32(86, 105, 126, 40);
    }

    private int DrawSceneTiles(ImDrawList* draw, Vector2 origin, SceneViewCamera.Projection view,
        LevelRenderer renderer, FloorRegion coverage, Vector2 cursor, bool pick)
    {
        if (!pick && (!_hasFloorSelection || _inspectDecoration)) return -1;
        Vector3d pivot = _sceneCamera.Pivot;
        _sceneTiles.Clear();
        var (first, last) = FloorRange;
        if (!pick && first == last)
        {
            _sceneCandidates.Clear();
            _sceneCandidates.Add(first);
        }
        else
            renderer.CollectSceneTiles(coverage, _floor, SceneTileLimit, _sceneCandidates);
        foreach (int index in _sceneCandidates)
        {
            if ((!pick && (index < first || index > last)) || !renderer.TryGetSceneTile(index, out var tile))
                continue;
            Vector3 local = new((float)(tile.Center.X - pivot.X), (float)(tile.Center.Y - pivot.Y), (float)-pivot.Z);
            _sceneTiles.Add(new(index, tile, local, view.Depth(local)));
        }
        if (pick)
            _sceneTiles.Sort(static (a, b) => a.Depth == b.Depth ? b.Index.CompareTo(a.Index) : b.Depth.CompareTo(a.Depth));
        int hit = -1;
        foreach (var tile in _sceneTiles)
        {
            bool selected = !_canvasOnly && !_inspectDecoration && _hasFloorSelection && tile.Index >= first && tile.Index <= last;
            if (DrawSceneTile(draw, origin, view, tile, selected, cursor, pick))
                hit = tile.Index;
        }
        return hit;
    }

    private bool DrawSceneTile(ImDrawList* draw, Vector2 origin, SceneViewCamera.Projection view,
        SceneDrawTile item, bool selected, Vector2 cursor, bool pick)
    {
        if (!_sceneShapes.TryGetValue(item.Tile.Shape, out var outline))
        {
            if (_sceneShapes.Count >= 2 * SceneTileLimit) _sceneShapes.Clear();
            _sceneShapes.Add(item.Tile.Shape, outline = item.Tile.Shape.Build().Collider);
        }
        if (outline.Length < 3) return false;
        Span<Vector3> face = stackalloc Vector3[outline.Length];
        (float sin, float cos) = MathF.SinCos(item.Tile.Rotation);
        for (int i = 0; i < face.Length; i++)
        {
            float x = outline[i].X * item.Tile.Scale.X;
            float y = outline[i].Y * item.Tile.Scale.Y;
            face[i] = item.Center + new Vector3(x * cos - y * sin, x * sin + y * cos, 0);
        }
        Span<Vector2> projected = stackalloc Vector2[face.Length * 2];
        int count = view.ClipPolygon(face, projected);
        if (count < 3) return false;
        projected = projected[..count];
        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var point in projected)
        {
            min = Vector2.ComponentMin(min, point);
            max = Vector2.ComponentMax(max, point);
        }
        if (max.X < 0 || max.Y < 0 || min.X > 2 * view.HalfSize.X || min.Y > 2 * view.HalfSize.Y)
            return false;
        if (selected)
        {
            for (int i = 0; i < count; i++)
                ImDrawList.AddLine(draw, ScenePoint(origin + projected[i]), ScenePoint(origin + projected[(i + 1) % count]),
                    SceneAccent, 1.8f);
            if (item.Index == _floor && _showBrickNumbers && view.ContainsDepth(item.Center))
                ImDrawList.AddText(draw, ScenePoint(origin + view.Project(item.Center) + new Vector2(7, -14)), SceneAccent,
                    item.Index.ToString());
        }
        if (!pick || cursor.X < min.X || cursor.X > max.X || cursor.Y < min.Y || cursor.Y > max.Y)
            return false;
        return SceneContains(projected, cursor);
    }

    private JObject? DrawSceneDecorations(ImDrawList* draw, Vector2 origin,
        LevelRenderer renderer, Vector2 cursor, bool pick, ImGuiIO* io)
    {
        if (!_inspectDecoration || _hideAllDecorations) return null;
        JObject? hit = null;
        int hitIndex = -1;
        var decorations = _document.Decorations;
        Span<Vector2> corners = stackalloc Vector2[4];
        Vector2 framebufferScale = new(io->DisplayFramebufferScale.x, io->DisplayFramebufferScale.y);
        foreach (int index in renderer.VisibleDecorationIndices)
        {
            if ((uint)index >= (uint)decorations.Count || decorations[index] is not JObject item || DecorationHidden(item))
                continue;
            bool selected = _selectedDecorations.Contains(item);
            bool canHit = pick && index > hitIndex && !DecorationLocked(item);
            if ((!selected && !canHit) || !renderer.TryGetDecorationQuad(index, corners)) continue;
            for (int i = 0; i < corners.Length; i++) corners[i] /= framebufferScale;
            if (selected)
                for (int i = 0; i < corners.Length; i++)
                    ImDrawList.AddLine(draw, ScenePoint(origin + corners[i]), ScenePoint(origin + corners[(i + 1) % corners.Length]),
                        SceneAccent, 1.5f);
            if (canHit && InsideQuad(corners, cursor))
            {
                hit = item;
                hitIndex = index;
            }
        }
        return hit;
    }

    private static Vector3d SceneDecorationPosition(LevelRenderer renderer, JObject item)
    {
        Vector2 position = DecorationPosition(renderer, item);
        double depth = InspectorFields.TryBoolean(item["syncFloorDepth"], out bool synced) && synced
            ? 0 : item.Value<double?>("depth") ?? 0;
        return new Vector3d(position.X, position.Y, SceneRenderView.LayerHeight(depth, renderer.TileSpacing));
    }

    private Vector2 SceneAxisPoint(SceneViewCamera.Projection view, Vector2 center, int axis) =>
        center + new Vector2(Vector3.Dot(SceneAxes[axis], view.Right), -Vector3.Dot(SceneAxes[axis], view.Up)) * (32 * _uiScale);

    private int? FindSceneAxis(SceneViewCamera.Projection view, Vector2 center, Vector2 cursor)
    {
        int? hit = null;
        float threshold = 12 * _uiScale;
        float best = threshold * threshold;
        for (int i = 0; i < SceneAxes.Length; i++)
        {
            float distance = Vector2.DistanceSquared(cursor, SceneAxisPoint(view, center, i));
            if (distance <= best) { best = distance; hit = i; }
        }
        return hit;
    }

    private void DrawSceneAxes(ImDrawList* draw, Vector2 origin, SceneViewCamera.Projection view, Vector2 center)
    {
        ImDrawList.AddCircleFilled(draw, ScenePoint(origin + center), 46 * _uiScale, WidgetDraw.U32(15, 22, 31, 238), 32);
        for (int i = 0; i < SceneAxes.Length; i++)
        {
            Vector2 point = origin + SceneAxisPoint(view, center, i);
            float radius = 9 * _uiScale;
            ImDrawList.AddLine(draw, ScenePoint(origin + center), ScenePoint(point), SceneAxisColors[i], 2);
            ImDrawList.AddCircleFilled(draw, ScenePoint(point), radius, SceneAxisColors[i], 16);
            Vector2 padding = new(radius);
            WidgetDraw.TextCentered(draw, ScenePoint(point - padding), ScenePoint(point + padding),
                WidgetDraw.U32(21, 28, 37), SceneAxisLabels[i]);
        }
    }

    private static void DrawSceneLine(ImDrawList* draw, Vector2 origin, SceneViewCamera.Projection view,
        Vector3 a, Vector3 b, uint color)
    {
        if (view.ClipLine(ref a, ref b))
            ImDrawList.AddLine(draw, ScenePoint(origin + view.Project(a)), ScenePoint(origin + view.Project(b)), color, 1f);
    }

    private static ImVec2 ScenePoint(Vector2 point) => WidgetDraw.V2(point.X, point.Y);

    internal static bool SceneContains(ReadOnlySpan<Vector2> points, Vector2 cursor)
    {
        bool inside = false;
        Vector2 last = points[^1];
        foreach (var next in points)
        {
            if ((last.Y > cursor.Y) != (next.Y > cursor.Y) &&
                cursor.X < (next.X - last.X) * (cursor.Y - last.Y) / (next.Y - last.Y) + last.X)
                inside = !inside;
            last = next;
        }
        return inside;
    }
}
