using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Thrust.Graphics;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    private readonly Dictionary<int, (FloorRenderTransform Transform, float Opacity)> _decorationParentStates = [];

// 渲染：每帧推进、绘制行星与拖尾、相机。
// Rendering: per-frame advance, planets and trail, camera.

    public int FloorBatchCount => _activeFloorChunks is { } chunks
        ? chunks.Sum(chunk => chunk.Renderer.BatchCount) : _officialRenderer?.BatchCount ?? 0;
    public int FloorDrawCalls => _chunkDrawCalls;

    /// <summary>
    /// 当前砖块那一拍结束时行星应到达的角度（度）。手打判定的参考角度。
    /// The angle the planet should reach at the end of the current tile's beat, in degrees.
    /// </summary>
    public double ReferenceAngleDegrees
    {
        get
        {
            if (_floors.Count == 0)
            {
                return 0.0;
            }

            int index = Math.Clamp(_currentIndex, 0, _floors.Count - 1);

            return _sweepStart.Length > index ? _sweepStart[index] + _sweepDelta[index] : 0.0;
        }
    }

    /// <summary>当前砖块的 BPM × 速度，判定窗口随它收紧。</summary>
    public double BpmTimesSpeed
    {
        get
        {
            if (_floors.Count == 0)
            {
                return 120.0;
            }

            int index = Math.Clamp(_currentIndex, 0, _floors.Count - 1);
            double bpm = _floors[index].bpm > 1e-6 ? _floors[index].bpm : 120.0;

            return bpm;
        }
    }

    /// <summary>Clear the trail state.</summary>
    private void ResetTrail()
    {
        _trail.Clear();
        _previousCurrentIndex = -1;
        _previousPlayTime = _playTime;
        _offlineTrailTime = double.NaN;
        _offlineTrailFloor = -1;
    }

    /// <summary>Pan by a screen-space pixel delta.</summary>
    public void Pan(Vector2 screenDelta)
    {
        // Panning is ignored during playback so the camera is not fought over
        if (_isPlaying)
        {
            return;
        }

        PanFreely(screenDelta);
    }

    internal bool CanNavigateEditorPlayback => _isPlaying && _editorEvents is not null && !_editorHasCameraMotion;

    internal void NavigateEditorPlayback(Vector2 screenDelta, float scroll, Vector2 cursor)
    {
        if (!CanNavigateEditorPlayback)
            return;

        if (screenDelta != Vector2.Zero)
            PanFreely(screenDelta);
        // Zoom on the follow target until the user explicitly pans away.
        Zoom(scroll, _cameraFree ? cursor : new Vector2(_camera.ViewportWidth, _camera.ViewportHeight) * 0.5f);
    }

    private void PanFreely(Vector2 screenDelta)
    {
        _cameraFree = true;
        _camera.Position += _camera.ScreenToWorld(Vector2.Zero) - _camera.ScreenToWorld(screenDelta);
        _camera.MarkDirty();
    }

    /// <summary>Zoom around the cursor.</summary>
    public void Zoom(float scroll, Vector2 screenPoint)
    {
        if (MathF.Abs(scroll) < 1e-4f)
        {
            return;
        }

        // Zooming while paused also takes control; during playback the follow logic owns the camera
        if (!_isPlaying)
        {
            _cameraFree = true;
        }

        Vector2 anchor = _camera.ScreenToWorld(screenPoint);
        _camera.Zoom = Math.Clamp(_camera.Zoom * MathF.Exp(scroll * 0.15f), _camera.MinZoom, _camera.MaxZoom);
        _camera.Position += anchor - _camera.ScreenToWorld(screenPoint);
        _camera.MarkDirty();
    }

    /// <summary>Restore the initial view.</summary>
    public void ResetView()
    {
        _cameraFree = false;
        _camera.Position = _homePosition;
        _camera.Zoom = _homeZoom;
        _camera.Rotation = _homeRotation;
        _camera.MarkDirty();
    }

    /// <summary>Update the viewport size.</summary>
    public void Resize(float width, float height)
    {
        float oldHeight = _camera.ViewportHeight;
        _camera.ViewportWidth = width;
        _camera.ViewportHeight = height;
        if (_settings.CameraZoom is float zoom)
        {
            _camera.Zoom = Math.Clamp(_camera.Zoom * _camera.ViewportHeight / oldHeight, _camera.MinZoom, _camera.MaxZoom);
            _homeZoom = AuthoredZoom(_camera.ViewportHeight, zoom);
        }
    }

    /// <summary>Advance frame timing and playback.</summary>
    public void Tick(double now)
    {
        // Commit a finished background load on the main thread
        PumpLoad();

        PumpEffectWarmup();

        // Shader hot reload: rebuild on the next frame
        if (_shaderReloadPending)
        {
            _shaderReloadPending = false;
            ApplyShaderReload();
        }

        double delta = now - _lastFrameTime;
        _lastFrameTime = now;

        if (delta <= 0.0 || delta > 1.0)
        {
            return;
        }

        // A ring buffer keeps the FPS math allocation free
        _frameTimeRing[_frameTimeIndex] = delta;
        _frameTimeSum += delta;
        _frameTimeIndex = (_frameTimeIndex + 1) % FrameTimeWindow;

        if (_frameTimeCount < FrameTimeWindow)
        {
            _frameTimeCount++;
        }
        else
        {
            // Subtract the value being overwritten
            int oldest = _frameTimeIndex;
            _frameTimeSum -= _frameTimeRing[oldest];
        }

        Fps = _frameTimeSum > 0.0 ? _frameTimeCount / _frameTimeSum : 0.0;

        // No timeline exists during a background load, so skip the playback evaluation
        if (IsLoading)
        {
            return;
        }

        UpdatePlayback(delta);
        SyncAudio();
    }

    /// <summary>Submit the draw calls.</summary>
    public void Render() => Render(null);

    internal void RenderAfterClear(Vector4 background) => Render(background);

    private void Render(Vector4? clearedBackground)
    {
        UpdateFilterClock();
        if (_preparingLevelEffects)
        {
            DrawCalls = 0;
            DrawnTriangles = 0;
            _filterBackend?.ObserveCameraFrame(_playTime);
            return;
        }

        float neutralZoom = _camera.Zoom;
        Vector2 neutralPosition = _camera.Position;
        bool shake = _visualSettings.CameraShake && !_cameraFree && _cameraShake != Vector2.Zero;
        if (shake)
        {
            _camera.Position += _cameraShake;
            _camera.MarkDirty();
        }
        float pulseScale = CameraPulseScale();
        if (pulseScale != 1f)
        {
            _camera.Zoom = neutralZoom * pulseScale;
            _camera.MarkDirty();
        }

        try
        {
            SceneColorHistory? history = null;
            if (_visualSettings.ScreenEffects && _backdrop?.UsesHistory == true)
                history = _colorHistory ??= new SceneColorHistory();
            else
                _colorHistory?.Reset();
            if (_backdrop?.HistoryAt(_playTime) == true)
            {
                history?.Restore(_playTime);
                clearedBackground = null;
            }

            DrawCalls = 0;
            DrawnTriangles = 0;
            _chunkDrawCalls = 0;
            // Observe OFF frames too, so a seek or resize cannot revive stale filter output.
            _filterBackend?.ObserveCameraFrame(_playTime);
            bool mainCamera = !_cameraRenderDisabled || _cameraFree;
            bool filtersDue = _visualSettings.CameraFilters && mainCamera && _filterTimeline?.HasDueEvent(_playTime) == true;
            bool backgroundDue = _visualSettings.CameraFilters && _backgroundFilterTimeline?.HasDueEvent(_playTime) == true;
            bool screenActive = _visualSettings.ScreenEffects && mainCamera && _sceneEffects?.HasScreenEffect(_playTime) == true;
            _scenePasses.Clear();
            _backgroundPasses = [];
            _fallingPetals = null;
            if (filtersDue || backgroundDue || screenActive)
            {
                var backend = _filterBackend ??= new Filters.OpenGlFilterBackend();
                backend.CameraView = _camera.ViewProjection;
                if (filtersDue)
                    foreach (var entry in _filterTimeline!.Evaluate(backend, _playTime))
                    {
                        if (entry.Filter is Filters.FallingPetalsFilter petals)
                            _fallingPetals = petals;
                        else
                            _scenePasses.Add(entry);
                    }
                if (backgroundDue)
                    _backgroundPasses = _backgroundFilterTimeline!.Evaluate(backend, _playTime);
                if (screenActive)
                {
                    _sceneEffects!.AppendScreenPasses(backend, _playTime, _scenePasses);
                    _scenePasses.Sort(static (left, right) => left.Order.CompareTo(right.Order));
                }
            }

            System.Numerics.Vector4? clearSource = null;
            if (clearedBackground == new Vector4(0f, 0f, 0f, 1f) &&
                (_backgroundPasses.Count > 0 || _scenePasses.Count > 0) &&
                !GL.IsEnabled(EnableCap.ScissorTest) && !GL.IsEnabled(EnableCap.FramebufferSrgb))
                clearSource = new System.Numerics.Vector4(0f, 0f, 0f, 1f);

            if (_backgroundPasses.Count == 0 && _scenePasses.Count > 0 && clearSource is not null &&
                BackgroundPlaneIsEmpty())
            {
                // Render directly into the filter source when the viewport has
                // only its clear color. A window-to-FBO import can stall the driver.
                _filterBackend!.Draw(_playTime, _filterClock, _filterDelta, _scenePasses,
                    RenderMainCamera, clearSource: clearSource);
            }
            else
            {
                // Draw the background image or tutorial layers before compositing the main camera.
                if (_backgroundPasses.Count > 0)
                    _filterBackend!.Draw(_playTime, _filterClock, _filterDelta, _backgroundPasses,
                        DrawBackgroundPlane, background: true, clearSource: clearSource);
                else
                    DrawBackgroundPlane();

                if (mainCamera)
                {
                    if (_scenePasses.Count > 0)
                        _filterBackend!.Draw(_playTime, _filterClock, _filterDelta, _scenePasses, RenderMainCamera);
                    else
                        RenderMainCamera();
                }
            }
            history?.Capture(_playTime);
        }
        finally
        {
            if (shake)
            {
                _camera.Position = neutralPosition;
                _camera.MarkDirty();
            }
            if (pulseScale != 1f)
            {
                _camera.Zoom = neutralZoom;
                _camera.MarkDirty();
            }
        }
    }

    private Filters.FallingPetalsFilter? _fallingPetals;

    private void UpdateFilterClock()
    {
        if (_offlineSubSample)
        {
            double step = (_playTime - _lastFilterTime) / _pitch;
            _filterDelta = double.IsFinite(step) && step >= 0 ? (float)step : 0f;
            _filterClock = double.IsFinite(step) && step >= 0 ? _filterClock + step : 0;
        }
        else
        {
            double step = _lastFrameTime - _lastFilterWallTime;
            _filterDelta = !_filterClockPaused && double.IsFinite(step) ? (float)Math.Clamp(step, 0, 1.0 / 3) : 0f;
            _liveFilterClock += _filterDelta;
            _filterClock = _liveFilterClock;
        }
        _lastFilterWallTime = _lastFrameTime;
        _lastFilterTime = _playTime;
    }

    private void RenderMainCamera() => RenderMainCameraContents();

    internal void RenderEditorTiles()
    {
        _colorHistory?.Reset();
        _lastFilterTime = double.NaN;
        DrawCalls = 0;
        DrawnTriangles = 0;
        _chunkDrawCalls = 0;
        RenderMainCameraContents(tilesOnly: true);
    }

    private void RenderMainCameraContents(bool tilesOnly = false)
    {
        ImageDecorationRenderer? syncedSprites = tilesOnly ? null : PrepareAndDrawBackDecorations();

        EnsureVisibleFloors();
        if (_activeFloorChunks is null && _officialRenderer is not { HasGeometry: true } && EditorInsertedFloorCount == 0)
        {
            if (!tilesOnly)
                DrawFlash(foreground: true);
            return;
        }

        Matrix4 viewProjection = _camera.ViewProjection;

        // The floors use premultiplied alpha (Blend One, OneMinusSrcAlpha), so the
        // shadows composite correctly over the dark background
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        if (_floorVisualTime != _playTime)
        {
            UpdateTrackVisuals();
        }
        _officialRenderer?.PrepareLevelDraw(viewProjection, _camera.Position, (int)_camera.ViewportWidth, (int)_camera.ViewportHeight);

        int nextSynced = 0;
        if (_activeFloorChunks is { } chunks)
        {
            for (int i = chunks.Length - 1; i >= 0; i--)
            {
                _chunkDrawCalls += DrawFloorsWithDecorations(chunks[i].Renderer, chunks[i].Floors[0]);
            }
        }
        else if (_officialRenderer is not null)
        {
            _chunkDrawCalls = DrawFloorsWithDecorations(_officialRenderer, 0);
        }
        if (_editorInsertedTrack is { HasGeometry: true } inserted)
            _chunkDrawCalls += DrawFloors(inserted, 0, int.MaxValue);
        while (syncedSprites is not null && nextSynced < syncedSprites.SyncedCount)
            DrawCalls += syncedSprites.DrawSyncedFloor(_imageDecorations!, _camera, ref nextSynced);

        GL.Disable(EnableCap.Blend);
        if (tilesOnly)
            return;

        if (_trackGuideRenderer is { } guides)
        {
            guides.Draw(viewProjection, _runtimeTrackActive, _playTime);
            DrawCalls++;
        }

        // The planets draw over the track guides.
        if (_circleRenderer is not null && _floors.Count > 0)
        {
            DrawPlanets(viewProjection);
        }

        if (ShowDecorations && !_decorationShaderFailed && _imageDecorations is not null && _decorationRenderer is not null)
            DrawDecorations(background: false);
        if (_fallingPetals is { } petals && _spriteRenderer is { } particleSprites)
            DrawCalls += petals.Draw(particleSprites, _camera, _playTime, _pitch);
        DrawFlash(foreground: true);

        int DrawFloorsWithDecorations(OfficialFloorRenderer renderer, int firstFloor)
        {
            if (syncedSprites is null)
                return DrawFloors(renderer, 0, int.MaxValue);
            int from = 0;
            int floorDraws = 0;
            while (nextSynced < syncedSprites.SyncedCount &&
                syncedSprites.SyncedFloor(nextSynced) >= firstFloor)
            {
                int local = syncedSprites.SyncedFloor(nextSynced) - firstFloor;
                int end = local >= renderer.FloorCount ? 0 : renderer.IndexAfterFloor(local);
                if (end > from)
                {
                    floorDraws += DrawFloors(renderer, from, end);
                    from = end;
                }
                DrawCalls += syncedSprites.DrawSyncedFloor(_imageDecorations!, _camera, ref nextSynced);
            }
            return floorDraws + DrawFloors(renderer, from, int.MaxValue);
        }

        int DrawFloors(OfficialFloorRenderer renderer, int from, int end)
        {
            if (syncedSprites is not null)
                GL.Enable(EnableCap.Blend);
            renderer.Draw(viewProjection, _camera.Position, (int)_camera.ViewportWidth, (int)_camera.ViewportHeight,
                ForceFloorIconOutlines, from, end);
            DrawCalls += renderer.DrawCalls;
            DrawnTriangles += renderer.DrawnTriangles;
            return renderer.DrawCalls;
        }
    }

    private bool BackgroundPlaneIsEmpty()
    {
        if (_background is null || _backdrop is not { } backdrop)
            return true;
        if (_backgroundVideo is not null && backdrop.Video is { } video && BackgroundVideoTime(video) >= 0)
            return false;
        var state = backdrop.BackgroundAt(_playTime);
        return state.Pixels is null && !state.ShowsDefaultPattern &&
            (!_visualSettings.ScreenFlash || backdrop.FlashAt(_playTime, foreground: false).W <= 0f);
    }

    private void DrawBackgroundPlane()
    {
        if (_background is { } background && _backdrop is { } backdrop)
        {
            bool videoVisible = DrawBackgroundVideo(background);
            var state = backdrop.BackgroundAt(_playTime);
            var settings = state.Settings;
            if (_uploadedBackdrop != state)
            {
                background.SetImage(state.Pixels, settings.ImageSmoothing, IsBackgroundRepeated(settings));
                _uploadedBackdrop = state;
            }
            if (state.Pixels is not null || state.ShowsDefaultPattern && !videoVisible)
            {
                DrawCalls += background.Draw(settings, _camera, _cameraOrigin, state.Pixels is null ? null : DecorationShader());
            }
        }
        DrawFlash(foreground: false);
    }

    private static bool IsBackgroundRepeated(LevelPlaybackSettings settings) =>
        settings.LoopBackground || settings.Display == BackgroundDisplay.Tiled;

    private ImageDecorationRenderer? PrepareAndDrawBackDecorations()
    {
        if (!ShowDecorations || _imageDecorations is not { } decorations || _decorationShaderFailed)
            return null;
        try
        {
            if (DecorationShader() is { } sprites)
            {
                _decorationParentStates.Clear();
                sprites.Prepare(decorations, _playTime, _camera, TileSpacing,
                    AuthoredZoom(_camera.ViewportHeight, _settings.CameraZoom ?? 100f), DecorationFloor, DecorationPlanet,
                    CollectDecorationQuads, DecorationFloorOpacity, _visualSettings.VisibleDecorations,
                    ReadTrackVolume, ForceFloorIconOutlines, _runtimeTrackActive);
                DrawDecorations(background: true);
                return sprites.SyncedCount > 0 ? sprites : null;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            _decorationShaderFailed = true;
            _warnings.Add($"装饰着色器不可用：{error.Message}");
        }
        return null;
    }

    private ImageDecorationRenderer? DecorationShader()
    {
        if (_decorationShaderFailed)
            return null;
        try
        {
            return _decorationRenderer ??= new ImageDecorationRenderer(_officialRenderer);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            _decorationShaderFailed = true;
            _warnings.Add($"精灵着色器不可用：{error.Message}");
            return null;
        }
    }

    private void DrawFlash(bool foreground)
    {
        if (_visualSettings.ScreenFlash && _backdrop?.FlashAt(_playTime, foreground) is { W: > 0f } tint &&
            _background is { } background && DecorationShader() is { } sprites)
        {
            background.Flash(sprites, tint);
            DrawCalls++;
        }
    }

    private void DrawDecorations(bool background)
    {
        DrawCalls += _decorationRenderer!.Draw(_imageDecorations!, background, _camera);
    }

    public bool CollectDecorationQuads { get; set; }

    private bool CanReadDecorationQuads => CollectDecorationQuads && ShowDecorations && !_decorationShaderFailed &&
        !_preparingLevelEffects && (!_cameraRenderDisabled || _cameraFree);

    internal IEnumerable<int> VisibleDecorationIndices => CanReadDecorationQuads && _decorationRenderer is { } renderer
        ? renderer.VisibleEditorDecorations : [];

    public bool TryGetDecorationQuad(int index, Span<Vector2> pixels)
    {
        if (pixels.Length < 4 || !CanReadDecorationQuads ||
            _decorationRenderer is not { } renderer || !renderer.TryGetEditorQuad(index, out Matrix4 clip))
            return false;
        Project(-0.5f, -0.5f, out pixels[0]);
        Project(0.5f, -0.5f, out pixels[1]);
        Project(0.5f, 0.5f, out pixels[2]);
        Project(-0.5f, 0.5f, out pixels[3]);
        return true;

        void Project(float x, float y, out Vector2 point)
        {
            float clipX = x * clip.Row0.X + y * clip.Row1.X + clip.Row3.X;
            float clipY = x * clip.Row0.Y + y * clip.Row1.Y + clip.Row3.Y;
            point = new((1 + clipX) * _camera.ViewportWidth * 0.5f, (1 - clipY) * _camera.ViewportHeight * 0.5f);
        }
    }

    internal (Vector2 Center, Vector2 Scale, float Rotation) DecorationFloor(int index, bool dynamic)
    {
        if (_floors.Count == 0)
            return (Vector2.Zero, Vector2.One, 0f);
        index = Math.Clamp(index, 0, _floors.Count - 1);
        var position = _floors[index].position;
        var baseline = _runtimeTrackActive && _trackRuntime is { } runtime ? runtime.Baseline : _trackStates;
        var transform = dynamic ? DecorationParentState(index).Transform : baseline[index].Transform;
        return (new Vector2(position.X, position.Y) + transform.Offset, transform.Scale, transform.Rotation);
    }

    private Vector2 DecorationPlanet(string planet)
    {
        if ((uint)_currentIndex >= (uint)_planetTrack.Length)
            return Vector2.Zero;
        int id = planet == "RedPlanet" ? 1 : planet == "GreenPlanet" ? 2 : 0;
        var track = _planetTrack[_currentIndex];
        if (id == track.Pivot)
            return _planetCenter;
        if (id == track.Orbit)
            return _secondaryCenter;
        return track.Count == 3 && id == track.Third ? _thirdCenter : Vector2.Zero;
    }

    private float DecorationFloorOpacity(int index)
    {
        if (_floors.Count == 0)
            return 1f;
        index = Math.Clamp(index, 0, _floors.Count - 1);
        return DecorationParentState(index).Opacity;
    }

    private (FloorRenderTransform Transform, float Opacity) DecorationParentState(int index)
    {
        if (_runtimeTrackActive && _trackRuntime is { } runtime)
        {
            if (!_decorationParentStates.TryGetValue(index, out var state))
            {
                state = runtime.TransformAt(index, _playTime, _hidePassedFloors);
                _decorationParentStates.Add(index, state);
            }
            return state;
        }
        var preview = _trackStates[index];
        return (preview.Transform, preview.Hidden ? 0f : preview.Opacity);
    }

    /// <summary>
    /// Draw both planets and their particle trails. The planets use an 11-frame sprite
    /// animation; the trail reproduces the <c>PlanetRed/Pivot/Tail</c> particle system, where
    /// particles stay at their spawn point, shrink from full size to zero, and fade from red to a
    /// dark transparent red.
    /// </summary>
    private static Vector4 PlanetTint(int id) => id switch
    {
        1 => new Vector4(1f, 0f, 0f, 1f),
        2 => new Vector4(.92f, .94f, 1f, 1f),
        _ => new Vector4(0f, 0f, 1f, 1f),
    };

    private Vector4 PlanetBodyTint(int id) => id switch
    {
        1 => _firePlanetTint,
        0 => _icePlanetTint,
        _ => PlanetTint(id),
    };

    private void DrawPlanets(in Matrix4 viewProjection)
    {
        PlanetTrackFloor track = _planetTrack[_currentIndex];
        Vector4 primary = PlanetBodyTint(track.Pivot);
        Vector4 onOrbit = PlanetBodyTint(track.Orbit);
        float pivotScale = _sceneEffects?.PlanetScale(track.Pivot, _playTime) ?? 1f;
        float orbitScale = _sceneEffects?.PlanetScale(track.Orbit, _playTime) ?? 1f;
        float thirdScale = track.Count == 3 ? _sceneEffects?.PlanetScale(track.Third, _playTime) ?? 1f : 1f;

        if (_spriteRenderer is null)
        {
            float fallbackRadius = Floor.width;
            _circleRenderer!.Draw(viewProjection, _secondaryCenter, fallbackRadius * orbitScale, onOrbit);
            _circleRenderer.Draw(viewProjection, _planetCenter, fallbackRadius * pivotScale, primary);
            if (track.Count == 3)
            {
                _circleRenderer.Draw(viewProjection, _thirdCenter, fallbackRadius * thirdScale, PlanetBodyTint(track.Third));
            }
            DrawCalls += track.Count;
            return;
        }

        SpriteRenderer sprites = _spriteRenderer;

        // 拖尾按「具体星球」记录连续轨迹，再按弧长重采样（见 PlanetTrail）。它跟踪的是**某一颗**
        // 星球而不是「副星」角色：两颗交换位置时坐标重合，各自的路径天然连续，而副星角色每块砖
        // 都会瞬移到对侧，永远接不上。
        // The trail records each planet's own continuous path and resamples it by arc length (see
        // PlanetTrail). It tracks a **specific planet** rather than the "secondary" role: when the two
        // trade places their positions coincide, so each planet's path is naturally continuous, whereas
        // the secondary role teleports to the opposite side every tile and can never join up.
        //
        // 拖尾只在时间前进时记录：重复渲染同一时刻（暂停、或导出时 UI 那一帧）不该产生新点，
        // 否则同一位置会堆出重复采样，并在换砖处画出虚假的转折。
        // Record only when time advances: re-rendering the same instant (paused, or the UI frame during
        // an interactive export) must not add points, or duplicates pile up and fake corners appear at
        // tile changes.
        if (_trail.ActiveLifetime > 0 && _playTime > _previousPlayTime)
        {
            if (!_offlineSubSample)
                RecordTrailPose(_currentIndex, _planetCenter, _secondaryCenter, _thirdCenter, _playTime,
                    ref _previousCurrentIndex, preserveHistory: false);
            _previousCurrentIndex = _currentIndex;
            _previousPlayTime = _playTime;
        }

        ReadOnlySpan<PlanetTrail.Sample> samples = _trail.ActiveLifetime > 0
            ? _trail.Build(_playTime, FloorRegion.FromView(_camera).Expand(Vector2.One)) : [];

        if (!samples.IsEmpty)
        {
            if (_trailParticles.Length < samples.Length)
            {
                _trailParticles = new SpriteRenderer.Particle[Math.Max(samples.Length, _trailParticles.Length * 2)];
            }
            for (int i = 0; i < samples.Length; i++)
            {
                ref readonly PlanetTrail.Sample sample = ref samples[i];
                _trailParticles[i] = new SpriteRenderer.Particle
                {
                    Center = sample.Center,
                    HalfSize = new Vector2(sample.Size * .5f),
                    Tint = sample.Tint,
                };
            }

            sprites.DrawParticles(viewProjection, _trailParticles.AsSpan(0, samples.Length));
            DrawCalls += 1;
        }

        // The planets themselves
        int frame = PlanetFrame();

        // The diameter is the tile's short side (a tile is 2*Floor.length by 2*Floor.width, so the
        // short side is the height). The planet shader maps the sphere's rect inside the frame cell
        // onto the whole quad, so the quad size is the sphere's diameter.
        float diameter = Floor.width * 2f;
        Vector2 halfSize = new(diameter * 0.5f);

        Span<SpriteRenderer.Planet> planets = stackalloc SpriteRenderer.Planet[track.Count];
        planets[0] = new SpriteRenderer.Planet
        {
            Center = _secondaryCenter,
            HalfSize = halfSize * orbitScale,
            Rotation = RotationRadians(_rotationAngle),
            Tint = onOrbit,
            Frame = frame,
        };
        planets[1] = new SpriteRenderer.Planet
        {
            Center = _planetCenter,
            HalfSize = halfSize * pivotScale,
            Rotation = RotationRadians(_rotationAngle),
            Tint = primary,
            Frame = frame,
        };
        if (track.Count == 3)
        {
            planets[2] = planets[0];
            planets[2].Center = _thirdCenter;
            planets[2].HalfSize = halfSize * thirdScale;
            planets[2].Tint = PlanetBodyTint(track.Third);
        }

        sprites.DrawPlanets(viewProjection, planets);
        DrawCalls += 1;
    }

    /// <summary>
    /// The current animation frame, mirroring <c>PlanetSprite.LateUpdate</c>.
    /// </summary>
    /// <remarks>
    /// 相位取自播放时钟而不是墙上时钟：离线导出时墙上时钟随实际耗时乱走，行星会越转越快。
    /// 用播放时钟则无论在实时预览还是逐帧导出里，行星都稳定按 <see cref="PlanetAnimationFps"/> 转。
    /// The phase comes from the playback clock rather than the wall clock. During offline export the
    /// wall clock races with real elapsed time and the planet spins faster and faster; on the playback
    /// clock it always turns at exactly `PlanetAnimationFps`.
    /// </remarks>
    private int PlanetFrame()
    {
        if (_spriteRenderer is null)
        {
            return 0;
        }

        float time = (float)_playTime;
        return (int)(time * PlanetAnimationFps) % _spriteRenderer.FrameCount;
    }

    /// <summary>
    /// 退出离线模式：恢复实时预览的拖尾记录方式并清空历史。
    /// Leave offline mode: restore real-time trail recording and clear the history.
    /// </summary>
    public void EndOfflineRender()
    {
        if (_offlineSubSample)
        {
            SetRuntimeTrackMode(_runtimeTrackBeforeOffline);
            _floorVisualTime = double.NaN;
            ResetDecorationTriggers();
        }

        _offlineSubSample = false;
        ResetTrail();
    }

    internal void RestorePreviewAfterOfflineRender(double time)
    {
        _playTime = time;
        EndOfflineRender();
        _cameraSnap = true;
        _audioNeedsResync = true;
        ApplyTimeline(automaticConditions: true);
    }

    /// <summary>Degrees to radians.</summary>
    private static float RotationRadians(float degrees) => degrees * MathF.PI / 180f;

    /// <summary>Seconds the internal clock has been running.</summary>
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
}
