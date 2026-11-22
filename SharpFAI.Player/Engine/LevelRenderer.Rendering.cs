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
    }

    /// <summary>Pan by a screen-space pixel delta.</summary>
    public void Pan(Vector2 screenDelta)
    {
        // Panning is ignored during playback so the camera is not fought over
        if (_isPlaying)
        {
            return;
        }

        // Hand the camera over while paused so the timeline stops overwriting it and the user can
        // inspect the whole level freely
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
    public void Render()
    {
        float neutralZoom = _camera.Zoom;
        Vector2 neutralPosition = _camera.Position;
        bool shake = !_cameraFree && _cameraShake != Vector2.Zero;
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
            if (_backdrop?.HistoryAt(_playTime) == true)
                history = _colorHistory ??= new SceneColorHistory();
            else
                _colorHistory?.Reset();

            bool filtersDue = _filterTimeline?.HasDueEvent(_playTime) == true;
            bool screenActive = _sceneEffects?.HasScreenEffect(_playTime) == true;
            if (filtersDue || screenActive)
            {
                var backend = _filterBackend ??= new Filters.OpenGlFilterBackend();
                _scenePasses.Clear();
                if (filtersDue)
                    _scenePasses.AddRange(_filterTimeline!.Evaluate(backend, _playTime));
                if (screenActive)
                {
                    _sceneEffects!.AppendScreenPasses(backend, _playTime, _scenePasses);
                    _scenePasses.Sort(static (left, right) => left.Order.CompareTo(right.Order));
                }
                float delta = double.IsFinite(_lastFilterTime) ? (float)Math.Max(0, _playTime - _lastFilterTime) : 0;
                _lastFilterTime = _playTime;
                if (_scenePasses.Count > 0)
                {
                    backend.Draw(_playTime, delta, _scenePasses, RenderScene);
                    // The game's depth-only background clear leaves the previous
                    // camera output, including all post-processing, on the target.
                    history?.Capture(_playTime);
                    return;
                }
            }
            RenderScene();
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

    private void RenderScene()
    {
        _colorHistory?.Restore(_playTime);
        RenderSceneContents();
    }

    internal void RenderEditorTiles()
    {
        _colorHistory?.Reset();
        _lastFilterTime = double.NaN;
        RenderSceneContents(tilesOnly: true);
    }

    private void RenderSceneContents(bool tilesOnly = false)
    {
        DrawCalls = 0;
        DrawnTriangles = 0;
        if (!tilesOnly && _background is { } background && _backdrop is { } backdrop)
        {
            var state = backdrop.BackgroundAt(_playTime);
            var settings = state.Settings;
            if (_uploadedBackdrop != state)
            {
                background.SetImage(state.Pixels, settings.ImageSmoothing,
                    settings.LoopBackground || settings.Display == BackgroundDisplay.Tiled);
                _uploadedBackdrop = state;
            }
            if (state.Pixels is not null || settings.DefaultPattern)
            {
                background.Draw(settings, _camera, _cameraOrigin, state.Pixels is null ? null : DecorationShader());
                DrawCalls++;
            }
        }

        _chunkDrawCalls = 0;
        ImageDecorationRenderer? syncedSprites = null;
        if (!tilesOnly && ShowDecorations && _imageDecorations is { } decorations && !_decorationShaderFailed)
        {
            try
            {
                if (DecorationShader() is { } sprites)
                {
                    _decorationParentStates.Clear();
                    sprites.Prepare(decorations, _playTime, _camera, TileSpacing,
                        AuthoredZoom(_camera.ViewportHeight, _settings.CameraZoom ?? 100f), DecorationFloor, DecorationPlanet,
                        CollectDecorationQuads, DecorationFloorOpacity);
                    DrawDecorations(background: true);
                    if (sprites.SyncedCount != 0)
                        syncedSprites = sprites;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
            {
                _decorationShaderFailed = true;
                _warnings.Add($"装饰着色器不可用：{error.Message}");
            }
        }
        if (!tilesOnly)
            DrawFlash(foreground: false);

        EnsureVisibleFloors();
        if (_activeFloorChunks is null && _officialRenderer is not { HasGeometry: true })
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

        int nextSynced = 0;
        if (_activeFloorChunks is { } chunks)
        {
            for (int i = chunks.Length - 1; i >= 0; i--)
            {
                _chunkDrawCalls += DrawFloorsWithDecorations(chunks[i].Renderer, chunks[i].Floors[0]);
            }
        }
        else
        {
            _chunkDrawCalls = DrawFloorsWithDecorations(_officialRenderer!, 0);
        }
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

    private ImageDecorationRenderer? DecorationShader()
    {
        if (_decorationShaderFailed)
            return null;
        try
        {
            return _decorationRenderer ??= new ImageDecorationRenderer();
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
        if (_backdrop?.FlashAt(_playTime, foreground) is { W: > 0f } tint &&
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

    public bool TryGetDecorationQuad(int index, Span<Vector2> pixels)
    {
        if (pixels.Length < 4 || !CollectDecorationQuads || !ShowDecorations ||
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

    private (Vector2 Center, Vector2 Scale, float Rotation) DecorationFloor(int index, bool dynamic)
    {
        if (_floors.Count == 0)
            return (Vector2.Zero, Vector2.One, 0f);
        index = Math.Clamp(index, 0, _floors.Count - 1);
        var position = _floors[index].position;
        var transform = dynamic ? DecorationParentState(index).Transform : _trackStates[index].Transform;
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

    private void DrawPlanets(in Matrix4 viewProjection)
    {
        PlanetTrackFloor track = _planetTrack[_currentIndex];
        Vector4 primary = PlanetTint(track.Pivot);
        Vector4 onOrbit = PlanetTint(track.Orbit);
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
                _circleRenderer.Draw(viewProjection, _thirdCenter, fallbackRadius * thirdScale, PlanetTint(track.Third));
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
        if (_playTime > _previousPlayTime)
        {
            RecordTrailSamples(primary, onOrbit);
        }

        ReadOnlySpan<PlanetTrail.Sample> samples = _trail.Build(_playTime);

        if (!samples.IsEmpty)
        {
            if (_trailParticles.Length < samples.Length)
            {
                _trailParticles = new SpriteRenderer.Particle[Math.Max(samples.Length, _trailParticles.Length * 2)];
            }
            FloorRegion view = FloorRegion.FromView(_camera).Expand(Vector2.One);
            int written = 0;
            foreach (PlanetTrail.Sample sample in samples)
            {
                Vector2 particleHalfSize = new(sample.Size * 0.5f);
                if (!view.Intersects(new FloorRegion(sample.Center - particleHalfSize, sample.Center + particleHalfSize)))
                {
                    continue;
                }
                _trailParticles[written++] = new SpriteRenderer.Particle
                {
                    Center = sample.Center,
                    HalfSize = particleHalfSize,
                    Tint = sample.Tint,
                };
            }

            if (written > 0)
            {
                sprites.DrawParticles(viewProjection, _trailParticles.AsSpan(0, written));
                DrawCalls += 1;
            }
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
            planets[2].Tint = PlanetTint(track.Third);
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

    /// <summary>Records each planet's own path, with extra arc samples within an offline frame.</summary>
    private void RecordTrailSamples(Vector4 primaryTint, Vector4 secondaryTint)
    {
        PlanetTrackFloor track = _planetTrack[_currentIndex];
        bool crossed = _previousCurrentIndex != _currentIndex;
        if (crossed && (uint)_previousCurrentIndex < (uint)_planetTrack.Length)
        {
            int joined = ActivePlanets(track) & ~ActivePlanets(_planetTrack[_previousCurrentIndex]);
            for (int id = 0; id < 3; id++)
            {
                if ((joined & (1 << id)) != 0)
                {
                    _trail.ResetPlanet(id);
                }
            }
        }

        if (!_offlineSubSample || crossed)
        {
            Record(_planetCenter, _secondaryCenter, _thirdCenter, _playTime);
        }
        else
        {
            float previousRadius = Vector2.Distance(_previousSecondaryCenter, _previousPlanetCenter);
            float radius = Vector2.Distance(_secondaryCenter, _planetCenter);
            double delta = _sweepAngleNow - _previousAngleContinuous;
            int steps = Math.Clamp((int)(Math.Abs(delta) / TrailAngleStepDegrees) + 1, 1, 48);
            for (int step = 1; step <= steps; step++)
            {
                float fraction = step / (float)steps;
                Vector2 pivot = Vector2.Lerp(_previousPlanetCenter, _planetCenter, fraction);
                float orbitRadius = float.Lerp(previousRadius, radius, fraction);
                double time = _previousPlayTime + (_playTime - _previousPlayTime) * fraction;
                double angle = _previousAngleContinuous + delta * fraction;
                Vector2 orbit = pivot + PlanetTrackFloor.Direction(angle) * orbitRadius;
                Vector2 third = track.Count == 3
                    ? pivot + PlanetTrackFloor.Direction(angle + PlanetTrackFloor.ThirdAngle(_isCw)) * orbitRadius
                    : default;
                Record(pivot, orbit, third, time);
            }
        }

        SaveTrailFrame();

        void Record(Vector2 pivot, Vector2 orbit, Vector2 third, double time)
        {
            _trail.RecordPlanet(track.Pivot, pivot, primaryTint, time);
            _trail.RecordPlanet(track.Orbit, orbit, secondaryTint, time);
            if (track.Count == 3)
            {
                _trail.RecordPlanet(track.Third, third, PlanetTint(track.Third), time);
            }
        }
    }

    private static int ActivePlanets(PlanetTrackFloor track) =>
        (1 << track.Pivot) | (1 << track.Orbit) | (track.Count == 3 ? 1 << track.Third : 0);

    private void SaveTrailFrame()
    {
        _previousPlanetCenter = _planetCenter;
        _previousSecondaryCenter = _secondaryCenter;
        _previousAngleContinuous = _sweepAngleNow;
        _previousCurrentIndex = _currentIndex;
        _previousPlayTime = _playTime;
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
        ApplyTimeline();
    }

    /// <summary>Degrees to radians.</summary>
    private static float RotationRadians(float degrees) => degrees * MathF.PI / 180f;

    /// <summary>Seconds the internal clock has been running.</summary>
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
}
