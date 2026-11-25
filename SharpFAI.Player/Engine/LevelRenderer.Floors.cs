using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using Thrust.Geometry;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private const int ChunkDrawBudget = 40;
    private const int ChunkUploadBudget = 2;
    private const int FloorChunkCacheSize = 64;
    private const long FloorChunkCacheBytes = 128 * 1024 * 1024;

    private sealed record ResidentFloorChunk(int[] Floors, OfficialFloorRenderer Renderer)
    {
        public long LastUsed { get; set; }
    }

    private readonly Dictionary<int, ResidentFloorChunk> _floorChunks = [];
    private OfficialFloorMeshBaker.BakeCache _floorBakeCache = new();
    private ResidentFloorChunk[]? _activeFloorChunks;
    private int[] _activeFloorGroups = [];
    private long _floorChunkClock;
    private int _chunkDrawCalls;
    private int? _floorStreamTexels;
    private FloorTimeRange _residentTime = FloorTimeRange.Empty;

    /// <summary>
    /// Resolve the track style from the level settings, defaulting to Standard.
    /// </summary>
    private static TrackStyleInfo ReadTrackStyle(Level level)
    {
        try
        {
            return TrackStyleInfo.Parse(level.GetSetting<string>("trackStyle"));
        }
        catch (Exception)
        {
            return TrackStyleInfo.Standard;
        }
    }

    /// <summary>The active track style.</summary>
    public TrackStyleInfo Style => _trackStyle;

    /// <summary>Shows outlines even for floors without them enabled in the level.</summary>
    public bool ForceFloorIconOutlines { get; set; }

    /// <summary>Selected floor index, -1 when none.</summary>
    public int SelectedFloor => _selectedFloor;

    /// <summary>Every style the editor can render.</summary>
    public static IReadOnlyList<TrackStyleInfo> AvailableStyles => TrackStyleInfo.All;

    /// <summary>Frame the camera on the whole level.</summary>
    private void FrameLevel(List<Floor> floors)
    {
        if (!_hasBounds)
        {
            return;
        }

        Aabb bounds = _levelBounds;
        Vector2 min = new(bounds.Min.X, bounds.Min.Y);
        Vector2 max = new(bounds.Max.X, bounds.Max.Y);

        _camera.FrameWorldRect(min, max, 1.05f);

        // A tile spans roughly 150 units; below viewport/600 they blur together
        float minimumLegibleZoom = MathF.Max(_camera.ViewportWidth / 600f, 1e-4f);
        if (_camera.Zoom < minimumLegibleZoom)
        {
            _camera.Zoom = minimumLegibleZoom;
        }

        // Start at the first floor
        if (floors.Count > 0 && TryGetFloorCenter(0, out Vector2 firstCenter))
        {
            _cameraOrigin = firstCenter;
        }
        else
        {
            _cameraOrigin = (min + max) * 0.5f;
        }

        _camera.Position = LevelCameraTarget(_cameraOrigin);
        _cameraFrom = _cameraTo = _camera.Position;
        _camera.Rotation = _settings.CameraRotation;
        if (_settings.CameraZoom is float zoom)
        {
            _camera.Zoom = AuthoredZoom(_camera.ViewportHeight, zoom);
        }

        _camera.MarkDirty();

        _homePosition = _camera.Position;
        _homeZoom = _camera.Zoom;
        _homeRotation = _camera.Rotation;
    }

    /// <summary>Preview a different style without changing the chart or playback position.</summary>
    public void SetStyle(TrackStyleInfo style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (_officialRenderer is null || _floors.Count == 0 || style == _trackStyle)
        {
            return;
        }

        TrackStyleInfo? styleOverride = _level is { } level && style == ReadTrackStyle(level) ? null : style;
        try
        {
            string? path = LevelPath ?? _level?.pathToLevel;
            string? directory = path is null ? null : Path.GetDirectoryName(Path.GetFullPath(path));
            if (_visibleFloorIndex is not null)
            {
                VisibleFloorIndex next = VisibleFloorIndex.Build(_floors, _trackStates, _trackRuntime!,
                    _trackMaterialOptions, CancellationToken.None, styleOverride, _floorRenderTransforms);
                ClearFloorChunks();
                SetResidentGeometry(next, ResidentCoverage(), styleOverride, directory);
                _visibleFloorIndex = next;
                _levelBounds = next.Bounds;
            }
            else
            {
                FloorGeometry geometry = OfficialFloorMeshBaker.BakeLevel(_floors, _trackStates, _floorTextureCapacity,
                    levelDirectory: directory, materialOptions: _trackMaterialOptions, styleOverride: styleOverride);
                CommitFloorGeometry(geometry, preserveTransforms: true);
            }
            _styleOverride = styleOverride;
            _trackStyle = style;
            _status = $"{Path.GetFileName(path)} — {_floors.Count:N0} 块地板 · {style.Name}";
        }
        catch (Exception ex)
        {
            _warnings.Add($"轨道样式更新失败: {ex.Message}");
        }
    }

    private void CommitFloorGeometry(FloorGeometry geometry, bool preserveTransforms = false,
        IReadOnlyDictionary<FloorTexture, ImageDecoder.RgbaImage>? preparedTextures = null)
    {
        try
        {
            _officialRenderer ??= new OfficialFloorRenderer(_uploader, _floorTextureCapacity);
            _officialRenderer.SetGeometry(geometry, preserveTransforms, preparedTextures: preparedTextures);
        }
        catch
        {
            geometry.Dispose();
            throw;
        }

        if (_visibleFloorIndex is null)
        {
            _levelBounds = geometry.Bounds;
            _hasBounds = geometry.IndexCount > 0;
        }
        _floorVisualTime = double.NaN;
    }

    private void CommitLoadedFloorGeometry(FloorGeometry geometry,
        IReadOnlyDictionary<FloorTexture, ImageDecoder.RgbaImage>? preparedTextures = null)
    {
        if (geometry.IsLevelStream && geometry.Stream is { } stream)
        {
            int capacity = _floorStreamTexels ??= FloorStreamGpu.GetTexelCapacity();
            if (Math.Max(stream.Shapes.TexelCount, stream.InstanceTexels) > capacity)
            {
                geometry.Dispose();
                return;
            }
        }
        CommitFloorGeometry(geometry, preparedTextures: preparedTextures);
    }

    private FloorRegion ResidentCoverage()
    {
        FloorRegion view = FloorRegion.FromView(_camera);
        return view.Expand((view.Max - view.Min) * .30f + new Vector2(1f));
    }

    private void EnsureVisibleFloors()
    {
        if (_visibleFloorIndex is not { } visibility ||
            (_residentRegion.Contains(FloorRegion.FromView(_camera)) && _residentTime.Contains(_playTime)))
        {
            return;
        }

        string? directory = (LevelPath ?? _level?.pathToLevel) is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
        SetResidentGeometry(visibility, ResidentCoverage(), _styleOverride, directory);
    }

    private void SetResidentGeometry(VisibleFloorIndex visibility, FloorRegion coverage, TrackStyleInfo? style, string? directory)
    {
        FloorTimeRange time = _runtimeTrackActive ? new FloorTimeRange(_playTime, _playTime + .10) : FloorTimeRange.All;
        if (_officialRenderer is { } levelRenderer && levelRenderer.UsesLevelStream(visibility.Shapes))
        {
            levelRenderer.SetLevelSelection(visibility.Select(coverage, time));
            _residentFloors = null;
            _activeFloorChunks = null;
            _activeFloorGroups = [];
            _residentRegion = coverage;
            _residentTime = time;
            _floorVisualTime = double.NaN;
            return;
        }
        if (TrySetStreamedFloors(visibility, coverage, time, style, directory))
        {
            return;
        }

        int[] groups = visibility.SelectGroups(coverage, time, ChunkDrawBudget);
        if (groups.Length <= ChunkDrawBudget && TrySetFloorChunks(groups, style, directory))
        {
            _officialRenderer?.ClearGeometry();
            _residentFloors = null;
        }
        else
        {
            if (_runtimeTrackActive)
            {
                // On a large jump, bake the current view directly instead of uploading many cold chunks.
                FloorRegion view = FloorRegion.FromView(_camera);
                float pixelMargin = 2f / MathF.Max(1f, MathF.Min(_camera.ViewportWidth, _camera.ViewportHeight));
                coverage = view.Expand((view.Max - view.Min) * pixelMargin + Vector2.One);
                time = new FloorTimeRange(_playTime, _playTime + .02);
            }
            int[] selected = visibility.Select(coverage, time);
            if (_residentFloors is null || !_residentFloors.AsSpan().SequenceEqual(selected))
            {
                CommitResidentGeometry(selected, style, directory);
            }
            _activeFloorChunks = null;
            _activeFloorGroups = [];
        }
        _residentRegion = coverage;
        _residentTime = time;
    }

    private bool TrySetStreamedFloors(VisibleFloorIndex visibility, FloorRegion coverage, FloorTimeRange time,
        TrackStyleInfo? style, string? directory)
    {
        int capacity = _floorStreamTexels ??= FloorStreamGpu.GetTexelCapacity();
        FloorShapeLibrary shapes = visibility.Shapes;
        if (!shapes.Available || shapes.TexelCount > capacity || capacity == 0)
        {
            return false;
        }

        if (_runtimeTrackActive && Math.Abs(_currentIndex - _previousCurrentIndex) > 64)
        {
            FloorRegion view = FloorRegion.FromView(_camera);
            float margin = 2f / MathF.Max(1f, MathF.Min(_camera.ViewportWidth, _camera.ViewportHeight));
            coverage = view.Expand((view.Max - view.Min) * margin + Vector2.One);
            time = new FloorTimeRange(_playTime, _playTime + .025);
        }

        int[] selected = visibility.Select(coverage, time);
        if (_officialRenderer?.UsesStream != true || _residentFloors is null || !_residentFloors.AsSpan().SequenceEqual(selected))
        {
            int maxInstances = Math.Min(capacity / FloorStreamFrame.TexelsPerInstance,
                int.MaxValue >> FloorShapeLibrary.VertexBits);
            int instances = selected.Length;
            foreach (int floor in selected)
            {
                instances += _trackMaterialOptions[floor]?.Count ?? 0;
            }
            if (instances > maxInstances)
            {
                return false;
            }

            CommitFloorGeometry(OfficialFloorMeshBaker.BakeLevel(_floors, _trackStates, _floorTextureCapacity,
                levelDirectory: directory, materialOptions: _trackMaterialOptions, styleOverride: style, floorIndices: selected,
                cache: _floorBakeCache, computeBounds: false, shapes: shapes));
            _residentFloors = selected;
        }

        _activeFloorChunks = null;
        _activeFloorGroups = [];
        _residentRegion = coverage;
        _residentTime = time;
        return true;
    }

    private bool TrySetFloorChunks(int[] groups, TrackStyleInfo? style, string? directory)
    {
        if (_activeFloorChunks is not null && _activeFloorGroups.AsSpan().SequenceEqual(groups))
        {
            return true;
        }

        _officialRenderer ??= new OfficialFloorRenderer(_uploader, _floorTextureCapacity);
        ResidentFloorChunk[] active = new ResidentFloorChunk[groups.Length];
        List<(int Index, int[] Floors)> missing = [];
        for (int i = 0; i < groups.Length; i++)
        {
            if (_floorChunks.TryGetValue(groups[i], out ResidentFloorChunk? chunk))
            {
                active[i] = chunk;
            }
            else
            {
                int start = groups[i] * VisibleFloorIndex.FloorsPerGroup;
                missing.Add((i, Enumerable.Range(start, Math.Min(VisibleFloorIndex.FloorsPerGroup, _floors.Count - start)).ToArray()));
            }
        }

        if (_runtimeTrackActive && missing.Count > ChunkUploadBudget)
        {
            return false;
        }

        FloorGeometry?[] baked = new FloorGeometry[missing.Count];
        try
        {
            if (missing.Count == 1)
            {
                baked[0] = BakeResidentFloors(missing[0].Floors, style, directory);
            }
            else if (missing.Count > 1)
            {
                Parallel.For(0, missing.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    i => baked[i] = BakeResidentFloors(missing[i].Floors, style, directory));
            }

            int batches = 0;
            foreach (ResidentFloorChunk? chunk in active)
            {
                batches += chunk?.Renderer.BatchCount ?? 0;
            }
            foreach (FloorGeometry? geometry in baked)
            {
                batches += geometry!.Batches.Count;
            }
            if (batches > ChunkDrawBudget)
            {
                return false;
            }

            for (int i = 0; i < missing.Count; i++)
            {
                (int index, int[] floors) = missing[i];
                var renderer = new OfficialFloorRenderer(_uploader, _floorTextureCapacity, _officialRenderer);
                renderer.SetGeometry(baked[i]!, releaseCpuMesh: true);
                baked[i] = null;
                active[index] = new ResidentFloorChunk(floors, renderer);
                _floorChunks.Add(groups[index], active[index]);
            }
        }
        finally
        {
            foreach (FloorGeometry? geometry in baked)
            {
                geometry?.Dispose();
            }
        }

        long stamp = ++_floorChunkClock;
        foreach (ResidentFloorChunk chunk in active)
        {
            chunk.LastUsed = stamp;
        }
        long bytes = _floorChunks.Values.Sum(chunk => chunk.Renderer.GeometryBytes);
        while (_floorChunks.Count > FloorChunkCacheSize || bytes > FloorChunkCacheBytes)
        {
            var oldest = _floorChunks.MinBy(item => item.Value.LastUsed);
            if (oldest.Value.LastUsed == stamp)
            {
                break;
            }
            _floorChunks.Remove(oldest.Key);
            bytes -= oldest.Value.Renderer.GeometryBytes;
            oldest.Value.Renderer.Dispose();
        }

        _activeFloorChunks = active;
        _activeFloorGroups = groups;
        _floorVisualTime = double.NaN;
        return true;
    }

    private void ClearFloorChunks()
    {
        foreach (ResidentFloorChunk chunk in _floorChunks.Values)
        {
            chunk.Renderer.Dispose();
        }
        _floorChunks.Clear();
        _floorBakeCache = new OfficialFloorMeshBaker.BakeCache();
        _activeFloorChunks = null;
        _activeFloorGroups = [];
        _residentFloors = null;
        _residentRegion = FloorRegion.Empty;
        _residentTime = FloorTimeRange.Empty;
        _chunkDrawCalls = 0;
    }

    private void CommitResidentGeometry(int[] selected, TrackStyleInfo? style, string? directory)
    {
        CommitFloorGeometry(BakeResidentFloors(selected, style, directory));
        _residentFloors = selected;
    }

    private FloorGeometry BakeResidentFloors(int[] selected, TrackStyleInfo? style, string? directory) =>
        OfficialFloorMeshBaker.BakeLevel(_floors, _trackStates, _floorTextureCapacity,
            levelDirectory: directory, materialOptions: _trackMaterialOptions, styleOverride: style, floorIndices: selected,
            cache: _floorBakeCache, computeBounds: false);

    /// <summary>Overrides visual transforms for preview. An empty span restores the chart transforms.</summary>
    public void SetFloorRenderTransforms(ReadOnlySpan<FloorRenderTransform> transforms)
    {
        if (transforms.IsEmpty)
        {
            _floorRenderTransforms = null;
        }
        else
        {
            if (transforms.Length != FloorCount)
            {
                throw new ArgumentException("Supply one transform per floor.", nameof(transforms));
            }

            _floorRenderTransforms ??= new FloorRenderTransform[FloorCount];
            transforms.CopyTo(_floorRenderTransforms);
        }

        if (_visibleFloorIndex is not null)
        {
            _visibleFloorIndex = VisibleFloorIndex.Build(_floors, _trackStates, _trackRuntime!, _trackMaterialOptions,
                CancellationToken.None, _styleOverride, _floorRenderTransforms, _visibleFloorIndex.Shapes);
            _residentRegion = FloorRegion.Empty;
        }
        _floorVisualTime = double.NaN;
    }

    /// <summary>Overrides all floor colors for preview. An empty span restores chart colors.</summary>
    public void SetFloorRenderColors(ReadOnlySpan<Vector4> colors)
    {
        if (colors.IsEmpty)
        {
            _floorRenderColors = null;
        }
        else
        {
            if (colors.Length != FloorCount)
            {
                throw new ArgumentException("Supply one color per floor.", nameof(colors));
            }

            _floorRenderColors ??= new Vector4[FloorCount];
            colors.CopyTo(_floorRenderColors);
        }

        _floorVisualTime = double.NaN;
    }

    /// <summary>Return the frontmost floor under the screen point, or -1.</summary>
    public int PickFloor(Vector2 screenPoint) => PickFloorsCore(screenPoint, null);

    /// <summary>Replace the results with all floors under the screen point, front to back.</summary>
    public void PickFloors(Vector2 screenPoint, List<int> results)
    {
        results.Clear();
        PickFloorsCore(screenPoint, results);
        // Chunk residency is independent of the global floor sorting order.
        results.Sort();
    }

    private void EnsureFloorVisuals()
    {
        EnsureVisibleFloors();
        if (_floorVisualTime != _playTime)
            UpdateTrackVisuals();
    }

    private int PickFloorsCore(Vector2 screenPoint, List<int>? results)
    {
        EnsureFloorVisuals();
        Vector2 center = new(_camera.ViewportWidth * .5f, _camera.ViewportHeight * .5f);
        Vector2 cameraRelativePoint = _camera.ScreenDeltaToWorld(screenPoint - center);
        if (_activeFloorChunks is { } chunks)
        {
            foreach (ResidentFloorChunk chunk in chunks)
            {
                int picked = Pick(chunk.Renderer, chunk.Floors);
                if (picked >= 0)
                {
                    return picked;
                }
            }
        }
        else if (_officialRenderer is { HasGeometry: true } renderer)
        {
            return Pick(renderer, _residentFloors);
        }

        return -1;

        int Pick(OfficialFloorRenderer renderer, int[]? indices)
        {
            int[]? selection = renderer.LevelSelection;
            int count = selection?.Length ?? renderer.FloorCount;
            for (int candidate = 0; candidate < count; candidate++)
            {
                int i = selection?[candidate] ?? candidate;
                if (renderer.FloorOpacity(i) > 0f
                    && renderer.TryToFloorLocal(i, _camera.Position, cameraRelativePoint, out Vector2 local)
                    && PointInPolygon(renderer.GetOutline(i), local))
                {
                    int floor = indices?[i] ?? i;
                    if (results is null)
                        return floor;
                    results.Add(floor);
                }
            }
            return -1;
        }
    }

    public delegate void FloorOutlineVisitor(int floor, ReadOnlySpan<Vector2> screenOutline);

    /// <summary>Visit the projected outlines of resident floors in an inclusive range.</summary>
    public void VisitFloorOutlines(int first, int last, FloorOutlineVisitor visit)
    {
        if (first > last)
            return;
        EnsureFloorVisuals();
        (float sin, float cos) = MathF.SinCos(MathHelper.DegreesToRadians(_camera.Rotation));
        if (_activeFloorChunks is { } chunks)
        {
            foreach (ResidentFloorChunk chunk in chunks)
                Visit(chunk.Renderer, chunk.Floors);
        }
        else if (_officialRenderer is { HasGeometry: true } renderer)
            Visit(renderer, _residentFloors);

        void Visit(OfficialFloorRenderer renderer, int[]? indices)
        {
            int[]? selection = renderer.LevelSelection;
            int[]? visible = selection ?? indices;
            int start = visible is null ? Math.Max(first, 0) : Array.BinarySearch(visible, first);
            if (start < 0) start = ~start;
            Span<Vector2> buffer = stackalloc Vector2[128];
            int count = selection?.Length ?? renderer.FloorCount;
            for (int local = start; local < count; local++)
            {
                int i = selection?[local] ?? local;
                int floor = indices?[i] ?? i;
                if (floor > last) break;
                ReadOnlySpan<System.Numerics.Vector2> outline = renderer.GetOutline(i);
                if (outline.Length < 3) continue;
                Span<Vector2> projected = outline.Length <= buffer.Length ? buffer[..outline.Length] : new Vector2[outline.Length];
                renderer.ProjectFloorOutline(i, _camera.Position, outline, projected);
                Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                for (int j = 0; j < projected.Length; j++)
                {
                    Vector2 p = projected[j] * _camera.Zoom;
                    projected[j] = new(p.X * cos + p.Y * sin + _camera.ViewportWidth * .5f,
                        _camera.ViewportHeight * .5f - (-p.X * sin + p.Y * cos));
                    min = Vector2.ComponentMin(min, projected[j]);
                    max = Vector2.ComponentMax(max, projected[j]);
                }
                if (max.X >= 0 && max.Y >= 0 && min.X <= _camera.ViewportWidth && min.Y <= _camera.ViewportHeight)
                    visit(floor, projected);
            }
        }
    }

    /// <summary>
    /// World-space centre of a floor, used by the command-line capture to frame a specific tile.
    /// </summary>
    public bool TryGetFloorCenter(int index, out Vector2 center)
    {
        center = Vector2.Zero;

        if (index < 0 || index >= _floors.Count)
        {
            return false;
        }

        var position = _floors[index].position;
        FloorRenderTransform transform = _floorRenderTransforms?[index] ??
            (_runtimeTrackActive ? _trackRuntime!.TransformAt(index, _playTime, _hidePassedFloors).Transform
                : _trackStates[index].Transform);
        center = new Vector2(position.X, position.Y) + transform.Offset;
        return true;
    }

    /// <summary>Ray casting point-in-polygon test.</summary>
    private static bool PointInPolygon(ReadOnlySpan<System.Numerics.Vector2> polygon, Vector2 point)
    {
        if (polygon.Length < 3)
        {
            return false;
        }

        bool inside = false;

        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            System.Numerics.Vector2 a = polygon[i];
            System.Numerics.Vector2 b = polygon[j];

            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Select a floor, or pass -1 to clear. Only allowed while paused: selecting moves the playhead
    /// to that floor's start, so pressing play continues from the selected tile.
    /// </summary>
    public void SelectFloor(int index)
    {
        // Selection is locked during playback so the playhead cannot be yanked away mid-run
        if (_isPlaying)
        {
            return;
        }

        if (index < 0 || index >= _floors.Count)
        {
            _selectedFloor = -1;
            return;
        }

        _selectedFloor = index;

        if (_entryTimes.Length <= index || _floors.Count == 0)
        {
            return;
        }

        _playbackZoomNeedsReset = true;
        Pause();
        Seek(_entryTimes[index]);
    }
}
