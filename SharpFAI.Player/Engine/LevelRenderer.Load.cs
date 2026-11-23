using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Play;
using Thrust.Audio;
using Thrust.Geometry;
using Thrust.Graphics;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{

// 关卡加载：后台解析、上传、提交场景。
// Level loading: background parse, GPU upload, scene commit.

    internal string? LevelPath { get; private set; }
    private bool _keepSceneDuringLoad;

    internal bool ReloadLevel() => !IsLoading && LevelPath is { } path && Load(path);

    /// <summary>
    /// Load a level asynchronously: this returns at once; the background thread parses, meshes and
    /// synthesizes while <see cref="Tick"/> uploads and swaps the scene once it finishes.
    /// </summary>
    public bool Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _loadError = _status = "文件不存在";
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        return BeginLoad(fullPath, () => new Level(fullPath));
    }

    /// <summary>Load an independent in-memory chart snapshot using its source path for relative assets.</summary>
    public bool Load(Level snapshot, string sourcePath, bool keepEditorScene = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return BeginLoad(sourcePath, () => snapshot, keepEditorScene, editorSnapshot: true);
    }

    private bool BeginLoad(string path, Func<Level> readLevel, bool keepEditorScene = false, bool editorSnapshot = false)
    {
        path = Path.GetFullPath(path);
        // Cancel a load that is still running
        CancelPendingLoad();
        CancelEditorRevision();
        ClearEditorEffectUpdate();

        // Large levels already use staged GPU uploads and can require gigabytes of resident data.
        _keepSceneDuringLoad = keepEditorScene && _level is not null && _visibleFloorIndex is null;
        if (_keepSceneDuringLoad)
            Pause();
        else
            ClearScene();
        LevelPath = path;
        _warnings.Clear();
        _lastLoadTimings = [];
        _lastLoadTotalMs = 0.0;
        _loadError = null;
        _loadPhase = "启动中";
        _loadStartedAt = _clock.Elapsed.TotalSeconds;

        CancellationTokenSource cts = new();
        _loadCts = cts;

        _status = $"{Path.GetFileName(path)} — 加载中…";
        if (_floorTextureCapacity == 0)
        {
            _floorTextureCapacity = OfficialFloorRenderer.GetTextureCapacity();
        }

        int textureCapacity = _floorTextureCapacity;
        bool skipAudio = SkipAudio;
        CancellationToken token = cts.Token;
        _loadTask = Task.Run(() => BuildLevel(path, textureCapacity, token, readLevel, editorSnapshot, skipAudio), token);
        return true;
    }

    /// <summary>Cancel and await the background load.</summary>
    private void CancelPendingLoad()
    {
        _preparingLevelEffects = false;
        _shaderPrewarm = null;
        CancellationTokenSource? cts = _loadCts;
        _loadCts = null;

        Task<LevelLoadResult>? task = _loadTask;
        _loadTask = null;

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        // Wait for it to really exit so two loads never touch the same file. The worker only observes
        // cancellation at checkpoints, so this waits for the current step, not the whole load.
        if (task is not null)
        {
            try
            {
                if (task.Wait(TimeSpan.FromSeconds(2)))
                {
                    task.Result.Geometry?.Dispose();
                    task.Result.Guides?.Dispose();
                }
                else
                {
                    _ = task.ContinueWith(static completed =>
                    {
                        if (completed.IsCompletedSuccessfully)
                        {
                            completed.Result.Geometry?.Dispose();
                            completed.Result.Guides?.Dispose();
                        }
                        else
                        {
                            _ = completed.Exception;
                        }
                    }, TaskScheduler.Default);
                }
            }
            catch (AggregateException)
            {
                // A cancelled task throws; ignore it
            }
        }

        cts.Dispose();
    }

    /// <summary>
    /// Background worker: parse the level, build the mesh, compute the timeline, decode and synthesize
    /// the audio. All of it is CPU-only and never touches OpenGL.
    /// </summary>
    private LevelLoadResult BuildLevel(string path, int textureCapacity, CancellationToken token, Func<Level> readLevel,
        bool editorSnapshot, bool skipAudio)
    {
        List<(string Phase, double Milliseconds)> timings = [];
        System.Diagnostics.Stopwatch phaseClock = System.Diagnostics.Stopwatch.StartNew();

        // Close the previous phase and start the next one
        void MarkPhase(string phase)
        {
            phaseClock.Stop();
            if (!string.IsNullOrEmpty(_loadPhase))
            {
                timings.Add((_loadPhase, phaseClock.Elapsed.TotalMilliseconds));
            }

            _loadPhase = phase;
            phaseClock.Restart();
        }

        token.ThrowIfCancellationRequested();

        // First segment: from the worker starting to the level being parsed, thread scheduling included
        _loadPhase = "解析关卡";
        phaseClock.Restart();

        Level level = readLevel();
        Level playbackLevel = PlaybackEventBinder.ForPlayback(level);
        LevelPlaybackSettings settings = LevelPlaybackSettings.Read(level) with { SkipAudio = skipAudio };
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        token.ThrowIfCancellationRequested();
        MarkPhase("生成砖块");

        List<Floor> floors = CreatePlaybackFloors(playbackLevel, settings);
        token.ThrowIfCancellationRequested();
        MarkPhase("计算时间轴");
        PlaybackEvent[] events = editorSnapshot ? PlaybackEventBinder.BindForEditor(playbackLevel, level)
            : PlaybackEventBinder.BindForPlayback(playbackLevel);
        PlaybackTimeline timeline = BuildTimeline(playbackLevel, floors, events);
        var (floorPositionRemainders, trackStates, runtimeStates) = PlacePlaybackFloors(level, settings, events, floors, timeline, token);
        CameraTimeline? cameraTimeline = BuildCameraTimeline(events, floors, timeline, settings,
            Floor.length * 2f * settings.TileLength, runtimeStates);
        var filters = Filters.FilterTimeline.Build(events, floors, timeline.EntryTimes, timeline.FloorBpms,
            settings.LegacyFilterTween);
        var backgroundFilters = Filters.FilterTimeline.Build(events, floors, timeline.EntryTimes, timeline.FloorBpms,
            settings.LegacyFilterTween, background: true);
        var sceneEffects = SceneEffectTimeline.Build(events, floors, timeline.EntryTimes, timeline.FloorBpms);
        RuntimeTrackTimeline trackRuntime = new(events, floors, runtimeStates,
            timeline.EntryTimes, timeline.Pitch, timeline.FloorBpms);

        token.ThrowIfCancellationRequested();
        if (!settings.SkipAudio)
            MarkPhase("解码音频");
        // Audio decoding and decoration building are independent CPU work. Start decorations on a
        // worker with a small thread budget so the music decode keeps most cores instead of the two
        // halves starving each other.
        List<string> warnings = [];
        var rawSettings = level.root["settings"];
        bool legacyMasks = (rawSettings?["version"]?.ToObject<int>() ?? 0) < 15
            || rawSettings?["disableV15Features"]?.ToObject<bool>() == true;
        var decorationsTask = Task.Run(() => ImageDecorationTimeline.Build(events, floors, timeline.EntryTimes,
            timeline.FloorBpms, directory, warnings, token, legacyMasks, Floor.length * 2f * settings.TileLength,
            editable: editorSnapshot), token);
        (string? musicPath, DecodedAudio? music, DecodedAudio? hitsound) = BuildAudio(level, path, floors, timeline,
            settings, (phase, milliseconds) => timings.Add((phase, milliseconds)));
        MarkPhase("构建装饰与背景");
        var decorations = decorationsTask.GetAwaiter().GetResult();
        decorations?.BindCameraFilters(floors, timeline.FloorBpms, filters, backgroundFilters);
        var backdrop = BackdropTimeline.Build(settings, events, floors, timeline.EntryTimes, timeline.FloorBpms,
            directory, warnings);
        if (!string.IsNullOrWhiteSpace(settings.BackgroundVideo))
        {
            warnings.Add("当前播放器暂不播放关卡背景视频");
        }

        token.ThrowIfCancellationRequested();
        MarkPhase(floors.Count >= VisibleFloorIndex.MinimumResidentFloorCount ? "构建可见砖块索引" : "烘焙网格");
        var (materialOptions, visibility, geometry, guides) = BuildTrackGeometry(floors, trackStates, trackRuntime,
            timeline, settings, directory, textureCapacity, token);

        // Finish up: record the last phase and clear the running-phase text
        phaseClock.Stop();
        timings.Add((_loadPhase, phaseClock.Elapsed.TotalMilliseconds));
        _loadPhase = string.Empty;

        return new LevelLoadResult
        {
            Path = path,
            Level = level,
            Floors = floors,
            FloorPositionRemainders = floorPositionRemainders,
            Geometry = geometry,
            Visibility = visibility,
            Guides = guides,
            TrackStates = trackStates,
            TrackRuntime = trackRuntime,
            TrackMaterialOptions = materialOptions,
            Settings = settings,
            Backdrop = backdrop,
            Warnings = warnings,
            Timeline = timeline,
            EditorEvents = editorSnapshot ? events : null,
            CameraTimeline = cameraTimeline,
            FilterTimeline = filters,
            BackgroundFilterTimeline = backgroundFilters,
            SceneEffects = sceneEffects,
            Decorations = decorations,
            ManualTrack = new ManualTrack(floors, timeline, events),
            MusicPath = musicPath,
            Music = music,
            Hitsound = hitsound,
            Timings = timings,
        };
    }

    private static List<Floor> CreatePlaybackFloors(Level level, LevelPlaybackSettings settings)
    {
        var floors = level.CreateFloors(default, usePositionTrack: false, calculateLegacyEntryTimes: false);
        if (floors.Count < VisibleFloorIndex.MinimumFloorCount && settings.TileLength != 1f)
            foreach (var floor in floors) floor.position *= settings.TileLength;
        return floors;
    }

    private static (Vector2[]? Remainders, FloorTrackState[] States, FloorTrackState[] RuntimeStates) PlacePlaybackFloors(
        Level level, LevelPlaybackSettings settings, PlaybackEvent[] events, List<Floor> floors, PlaybackTimeline timeline,
        CancellationToken token)
    {
        float spacing = Floor.length * 2f * settings.TileLength;
        Vector2[]? remainders = null;
        if (floors.Count >= VisibleFloorIndex.MinimumFloorCount)
            remainders = BuildPreciseFloorPositions(floors, timeline.PlanetTrack, spacing, token);
        else
            PlanetTrackFloor.ApplySpacing(floors, timeline.PlanetTrack, spacing);
        var states = FloorTrackStateResolver.Resolve(level, floors, events, includeEditorOnly: true, timeline.FloorBpms);
        bool hasEditorOffsets = events.Any(item => item is PositionTrack position && position.IsActive &&
            position.Enabled("editorOnly", position.EditorOnly) == true);
        var runtimeStates = hasEditorOffsets
            ? FloorTrackStateResolver.Resolve(level, floors, events, includeEditorOnly: false, timeline.FloorBpms)
            : states;
        ApplyHoldIcons(floors, timeline.PlanetTrack, states);
        if (hasEditorOffsets) ApplyHoldIcons(floors, timeline.PlanetTrack, runtimeStates);
        ApplyTrackPlacement(floors, states, runtimeStates, remainders);
        return (remainders, states, runtimeStates);
    }

    private static (HashSet<FloorMaterial>?[] Materials, VisibleFloorIndex? Visibility, FloorGeometry? Geometry, Mesh? Guides)
        BuildTrackGeometry(List<Floor> floors, FloorTrackState[] states, RuntimeTrackTimeline runtime,
            PlaybackTimeline timeline, LevelPlaybackSettings settings, string? directory, int textureCapacity, CancellationToken token)
    {
        bool resident = floors.Count >= VisibleFloorIndex.MinimumResidentFloorCount;
        var materials = runtime.PrepareMaterials(token);
        var visibility = resident ? VisibleFloorIndex.Build(floors, states, runtime, materials, token) : null;
        FloorGeometry? geometry = null;
        if (!resident)
            geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, textureCapacity, token, directory, materials);
        else if (floors.Count is >= VisibleFloorIndex.MinimumFloorCount and <= 131072 &&
                 visibility!.Shapes.Available &&
                 CanBakeLevelStream(floors, states, materials, visibility.Shapes, token))
            geometry = OfficialFloorMeshBaker.BakeLevel(floors, states, textureCapacity, token, directory, materials,
                computeBounds: false, shapes: visibility.Shapes);
        try
        {
            var guides = TrackGuideMesh.Build(floors, states, runtime.Baseline, timeline, Floor.length * 2f * settings.TileLength, token);
            return (materials, visibility, geometry, guides);
        }
        catch
        {
            geometry?.Dispose();
            throw;
        }
    }

    private static bool CanBakeLevelStream(List<Floor> floors, FloorTrackState[] states,
        HashSet<FloorMaterial>?[] materials, FloorShapeLibrary shapes, CancellationToken token)
    {
        // Bound the persistent element buffer, including alternative track materials.
        const int maxIndices = 16 * 1024 * 1024;
        long indices = 0;
        for (int i = 0; i < floors.Count; i++)
        {
            if ((i & 255) == 0)
                token.ThrowIfCancellationRequested();
            FloorMaterial material = states[i].Material;
            indices += Count(material);
            if (materials[i] is { } options)
                foreach (FloorMaterial option in options)
                    if (option != material)
                        indices += Count(option);
            if (indices > maxIndices)
                return false;

            int Count(FloorMaterial value) => shapes[OfficialFloorMeshBaker.Shape.Create(floors[i], value)].Mesh!.Indices.Length;
        }
        return true;
    }

    /// <summary>
    /// Main thread: commit a finished background result to the scene (GPU upload plus state swap).
    /// </summary>
    private void CommitLevel(LevelLoadResult result)
    {
        System.Diagnostics.Stopwatch uploadClock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (result.Geometry is { } geometry)
            {
                CommitLoadedFloorGeometry(geometry);
            }
            if (result.Guides is { } guides)
            {
                _trackGuideRenderer = new TrackGuideRenderer(_uploader, guides);
            }
        }
        catch
        {
            result.Guides?.Dispose();
            throw;
        }
        _level = result.Level;
        _floors = result.Floors;
        SetEditorEvents(result.EditorEvents);
        _editorEventSlots = result.EditorEvents is null ? null : BuildSlots(result.EditorEvents);
        _editorLayerSlots = result.EditorEvents is null ? null : BuildLayerSlots(result.EditorEvents);
        _editorTimeline = result.EditorEvents is null ? null : result.Timeline;
        _floorPositionRemainders = result.FloorPositionRemainders;
        _trackStyle = ReadTrackStyle(result.Level);
        _trackStates = result.TrackStates;
        _trackMaterialOptions = result.TrackMaterialOptions;
        _trackRuntime = result.TrackRuntime;
        _cameraTimeline = result.CameraTimeline;
        _filterTimeline = result.FilterTimeline;
        _backgroundFilterTimeline = result.BackgroundFilterTimeline;
        _sceneEffects = result.SceneEffects;
        _imageDecorations = result.Decorations;
        _manualTrack = result.ManualTrack;
        _visibleFloorIndex = result.Visibility;
        if (_visibleFloorIndex is { } visibility)
        {
            _levelBounds = visibility.Bounds;
            _hasBounds = true;
        }
        _settings = result.Settings;
        _warnings.AddRange(result.Warnings);
        _backdrop = result.Backdrop;
        if (_backdrop.NeedsBackgroundRenderer)
            _background = new BackgroundRenderer();
        FloorCount = result.Floors.Count;

        // The GPU upload counts too: it runs on the main thread and is exactly the hitch the user feels
        result.Timings.Add(("上传 GPU", uploadClock.Elapsed.TotalMilliseconds));

        ApplyTimelineData(result.Timeline);
        ApplyAudio(result);
        if (_visibleFloorIndex is null)
        {
            UpdateTrackVisuals();
        }

        _circleRenderer ??= new CircleRenderer(ShaderLibrary.DiskDirectory);

        // Planet sprite renderer using the 11-frame animation; falls back to flat discs
        if (_spriteRenderer is null)
        {
            try
            {
                _spriteRenderer = new SpriteRenderer();
            }
            catch (Exception ex)
            {
                _warnings.Add($"星球贴图加载失败，改用纯色圆 / Planet texture failed, using flat discs: {ex.Message}");
                _spriteRenderer = null;
            }
        }

        InitializeShaderHotReload();
        ResetTrail();
        FrameLevel(result.Floors);
        if (_visibleFloorIndex is not null)
        {
            uploadClock.Restart();
            EnsureVisibleFloors();
            UpdateTrackVisuals();
            result.Timings.Add(("首屏烘焙和上传", uploadClock.Elapsed.TotalMilliseconds));
        }
        _selectedFloor = -1;

        _lastLoadTimings = result.Timings;
        _lastLoadTotalMs = 0.0;
        foreach ((string _, double milliseconds) in _lastLoadTimings)
        {
            _lastLoadTotalMs += milliseconds;
        }

        _status = $"{Path.GetFileName(result.Path)} — {result.Floors.Count:N0} 块地板";
        _loadPhase = string.Empty;
        _loadStartedAt = 0.0;
        BeginLevelEffectWarmup();
    }

    private void BeginLevelEffectWarmup()
    {
        BeginShaderPrewarm();
        _staticTextureIndex = 0;
        _prewarmedTextureBytes = 0;
        _preparingLevelEffects = _shaderPrewarm is not null || _imageDecorations is { Items.Count: > 0 } ||
            _componentPrewarm.Count > 0 || !_schedulesPrewarmed || !_scenePrewarmed;
        if (_preparingLevelEffects)
        {
            _loadPhase = "准备渲染资源";
            _loadStartedAt = _clock.Elapsed.TotalSeconds;
        }
    }

    private void BeginShaderPrewarm()
    {
        var foreground = (_filterTimeline?.ShaderNames() ?? []).Concat(_sceneEffects?.ShaderNames() ?? []).ToArray();
        var background = (_backgroundFilterTimeline?.ShaderNames() ?? []).ToArray();
        var programs = foreground.Concat(background).Distinct(StringComparer.Ordinal).ToArray();
        var textures = (_filterTimeline?.TextureNames() ?? []).Concat(_backgroundFilterTimeline?.TextureNames() ?? [])
            .Distinct(StringComparer.Ordinal);
        var jobs = new Queue<(string Name, int? Pass)>();
        foreach (string shader in programs)
            foreach (int pass in Filters.OpenGlFilterBackend.ShaderPasses(shader))
                jobs.Enqueue((shader, pass));
        foreach (string texture in textures)
            jobs.Enqueue((texture, null));

        _componentPrewarm.Clear();
        if (_filterTimeline is { } filters)
            foreach (string name in filters.ComponentNamesForWarmup())
                _componentPrewarm.Add((filters, name));
        if (_backgroundFilterTimeline is { } backgroundFilters)
            foreach (string name in backgroundFilters.ComponentNamesForWarmup())
                _componentPrewarm.Add((backgroundFilters, name));
        _componentPrewarmIndex = 0;
        _schedulesPrewarmed = _filterTimeline is null && _backgroundFilterTimeline is null;
        _scenePrewarmed = _sceneEffects is null;

        _shaderPrewarm = jobs.Count == 0 ? null : jobs;
        _shaderPrewarmTotal = jobs.Count;
        _shaderPrewarmDone = 0;
        _filterCamerasToPrewarm = (foreground.Length > 0 ? 1 : 0) + (background.Length > 0 ? 1 : 0);
        _prewarmBloom = programs.Contains(Filters.VideoBloomPass.ShaderName);
        _prewarmMotionBlur = programs.Contains(Filters.CameraMotionBlurFilter.ShaderName);
    }

    private void PumpEffectWarmup()
    {
        if (!_preparingLevelEffects && _shaderPrewarm is null)
            return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double budget = _preparingLevelEffects ? 6.0 : _isPlaying ? 1.0 : 4.0;
        while (_shaderPrewarm is { Count: > 0 } pending)
        {
            _filterBackend ??= new Filters.OpenGlFilterBackend();
            var (name, pass) = pending.Dequeue();
            if (pass is int shaderPass)
                _filterBackend.Prewarm(name, shaderPass);
            else
                _filterBackend.PrewarmTexture(name);
            _shaderPrewarmDone++;
            if (pending.Count == 0)
                _shaderPrewarm = null;
            if (clock.Elapsed.TotalMilliseconds >= budget)
                return;
        }

        if (!_preparingLevelEffects)
            return;

        // Instantiating and drawing each chart filter once moves its JIT work and the driver's
        // first-draw specialisation off the frame where the chart first switches effects.
        if (_componentPrewarmIndex < _componentPrewarm.Count || !_schedulesPrewarmed || !_scenePrewarmed)
        {
            var backend = _filterBackend ??= new Filters.OpenGlFilterBackend();
            using (new GlRenderStateScope())
            {
                if (!_schedulesPrewarmed)
                {
                    _filterTimeline?.PrewarmSchedule(backend);
                    _backgroundFilterTimeline?.PrewarmSchedule(backend);
                    _schedulesPrewarmed = true;
                }
                while (_componentPrewarmIndex < _componentPrewarm.Count)
                {
                    var (timeline, name) = _componentPrewarm[_componentPrewarmIndex++];
                    timeline.PrewarmComponent(backend, name, (int)_camera.ViewportWidth, (int)_camera.ViewportHeight);
                    if (clock.Elapsed.TotalMilliseconds >= budget)
                        return;
                }
                if (!_scenePrewarmed)
                {
                    _sceneEffects?.Prewarm(backend);
                    _scenePrewarmed = true;
                    if (clock.Elapsed.TotalMilliseconds >= budget)
                        return;
                }
            }
        }

        if (_imageDecorations is { Items.Count: > 0 } decorations && DecorationShader() is { } sprites)
        {
            // Limit speculative residency. Animated sequences still use their streaming pool.
            const long preloadBudget = 384L * 1024 * 1024;
            using var state = new GlRenderStateScope();
            while (_staticTextureIndex < decorations.StaticTextureUploads.Length)
            {
                var key = decorations.StaticTextureUploads[_staticTextureIndex++].Key;
                var image = decorations.Images[key.Image];
                long bytes = image.TextureBytes;
                if (_prewarmedTextureBytes + bytes > preloadBudget)
                    continue;
                sprites.PrewarmTexture(key, image);
                _prewarmedTextureBytes += bytes;
                if (clock.Elapsed.TotalMilliseconds >= budget)
                    return;
            }
        }
        if (_filterCamerasToPrewarm > 0)
            _filterBackend?.PrewarmTargets((int)_camera.ViewportWidth, (int)_camera.ViewportHeight,
                _filterCamerasToPrewarm, _prewarmBloom, _prewarmMotionBlur);
        OpenTK.Graphics.OpenGL4.GL.Finish();
        // Bulk image decoding leaves large temporary buffers behind. Reclaim them
        // during the loading screen on exceptionally large charts, before audio starts.
        if (_imageDecorations is { } loaded &&
            loaded.Images.Values.Sum(image => image.Pixels.LongLength) >= 1024L * 1024 * 1024)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        _preparingLevelEffects = false;
        _loadPhase = string.Empty;
        _loadStartedAt = 0.0;
    }

    /// <summary>
    /// Polls the background load once per frame and commits on the main thread so no GL call ever
    /// happens on the worker.
    /// </summary>
    private void PumpLoad()
    {
        Task<LevelLoadResult>? task = _loadTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        _loadTask = null;
        _loadCts?.Dispose();
        _loadCts = null;
        _loadPhase = string.Empty;
        _loadStartedAt = 0.0;
        bool keptScene = _keepSceneDuringLoad;
        _keepSceneDuringLoad = false;

        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            Exception ex = task.Exception?.GetBaseException() ?? new InvalidOperationException("加载失败");
            _loadError = ex.Message;
            _status = $"加载失败: {ex.Message}";
            _warnings.Add(ex.ToString());
            if (!keptScene)
                ClearScene();
            return;
        }

        try
        {
            if (keptScene)
                ClearScene();
            CommitLevel(task.Result);
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            _status = $"加载失败: {ex.Message}";
            _warnings.Add(ex.ToString());
            ClearScene();
        }
    }

    public void Unload()
    {
        CancelPendingLoad();
        ClearScene();
        _level = null;
        LevelPath = null;
        _loadError = null;
        _status = "未加载关卡";
        _loadPhase = string.Empty;
        _loadStartedAt = 0.0;
    }

    private void ClearScene()
    {
        CancelEditorRevision();
        ClearFloorChunks();
        _officialRenderer?.ClearLevel();
        _trackGuideRenderer?.Dispose();
        _trackGuideRenderer = null;
        _background?.Dispose();
        _background = null;
        _backdrop = null;
        _uploadedBackdrop = null;
        _colorHistory?.Dispose();
        _colorHistory = null;
        _settings = new LevelPlaybackSettings();
        _pitch = 1.0;

        FloorCount = 0;
        DrawCalls = 0;
        _hasBounds = false;
        _visibleFloorIndex = null;

        _floors = [];
        _floorPositionRemainders = null;
        _level = null;
        _trackStates = [];
        _floorRenderTransforms = null;
        _floorRenderColors = null;
        _trackRuntime = null;
        _trackMaterialOptions = [];
        _styleOverride = null;
        _runtimeTrackActive = false;
        _runtimeTrackBeforeOffline = false;
        _offlineSubSample = false;
        _floorVisualTime = double.NaN;
        _selectedFloor = -1;
        _entryTimes = [];
        _sweepStart = [];
        _sweepDelta = [];
        _planetTrack = [];
        _pauseAngles = null;
        _cameraTimeline = null;
        _filterTimeline?.Dispose();
        _filterTimeline = null;
        _backgroundFilterTimeline?.Dispose();
        _backgroundFilterTimeline = null;
        _backgroundPasses = [];
        _filterBackend?.ResetCameraHistory();
        _filterClockPaused = false;
        _shaderPrewarm = null;
        _shaderPrewarmTotal = 0;
        _shaderPrewarmDone = 0;
        _preparingLevelEffects = false;
        _sceneEffects?.Dispose();
        _sceneEffects = null;
        _cameraShake = Vector2.Zero;
        ResetDecorationTriggers();
        SetEditorEvents(null);
        _editorTimeline = null;
        ClearEditorEffectUpdate();
        _imageDecorations = null;
        _decorationRenderer?.Dispose();
        _decorationRenderer = null;
        _decorationShaderFailed = false;
        _scenePasses.Clear();
        _lastFilterTime = double.NaN;
        _cameraEventActive = false;
        _cameraEventZoomApplied = false;
        _currentIndex = 0;
        _playTime = 0.0;
        _duration = 0.0;
        _levelDuration = 0.0;
        _offsetSeconds = 0.0;
        _isPlaying = false;
        _manualTrack = null;
        ClearManualAttempt();
        ResetTrail();

        _music.Dispose();
        _hitsound.Dispose();
        _audioMusic = null;
        _audioHitsound = null;
        _audioMusicPath = null;
        _audioAvailable = false;
    }
}
