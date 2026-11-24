using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Play;
using Thrust.Audio;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{

// 播放控制：播放/暂停/跳转、手打判定、可见性。
// Playback control: play/pause/seek, manual play, tile visibility.

    private bool _playbackZoomNeedsReset = true;
    private bool _progressScrubbing;

    private float PlaybackBaseZoom => _settings.CameraZoom is float zoom
        ? AuthoredZoom(_camera.ViewportHeight, zoom) : _homeZoom;

    /// <summary>Start playback, from the selected floor when there is one.</summary>
    public void Play()
    {
        if (_floors.Count == 0)
        {
            return;
        }

        if (PlayInput.Enabled && _manualTrack is { LastFloor: >= 1 } track)
        {
            if (!PlaySession.IsTracking || PlaySession.Failed || PlaySession.Completed)
            {
                int start = PlaySession.Failed ? PlaySession.CheckpointFloor
                    : _selectedFloor >= 0 && _selectedFloor < track.LastFloor ? _selectedFloor : 0;
                PlayInput.ReleaseAll();
                PlayInput.Clear();
                ResetDecorationTriggers();
                PlaySession.Start(track, start);
                ResetCameraHit();
                _playTime = PlaySession.Time;
                if (start > 0)
                    _imageDecorations?.TriggerCondition(start, HitMargin.Perfect, _playTime, checkpoint: true);
                _cameraSnap = true;
                _playbackZoomNeedsReset = true;
            }
        }
        else if (_playTime >= _duration)
        {
            _playTime = 0.0;
            ResetDecorationTriggers();
            _playbackZoomNeedsReset = true;
        }

        if (_playbackZoomNeedsReset)
        {
            _camera.Zoom = PlaybackBaseZoom;
            _playbackZoomNeedsReset = false;
        }

        SetRuntimeTrackMode(true);
        _isPlaying = true;
        _filterClockPaused = false;
        _audioNeedsResync = true;

        if (_cameraFree)
        {
            _cameraFrom = _cameraEventActive ? _cameraFollowCenter : _camera.Position;
            _cameraElapsed = 0.0;
        }
        _cameraFree = false;
        _lastCameraTime = _playTime;
        ResetTrail();
        ApplyTimeline(automaticConditions: true);
    }

    /// <summary>Pause playback.</summary>
    public void Pause()
    {
        _isPlaying = false;
        _filterClockPaused = true;
        PlayInput.Clear();
        _music.Pause();
        _hitsound.Pause();
    }

    /// <summary>Toggle playback.</summary>
    public void TogglePlay()
    {
        if (_isPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    private bool ManualAttemptActive => PlayInput.Enabled && !_offlineSubSample && PlaySession.IsTracking;

    private string? ManualStatus
    {
        get
        {
            if (!ManualAttemptActive)
                return null;
            if (PlaySession.Failed && PlaySession.LastHit is { } hit)
            {
                string reason = PlaySession.FailedByHitbox ? "碰到装饰碰撞箱" :
                    PlaySession.ReleasedHoldEarly ? "长按提前松开" : HitJudge.Label(hit.Margin);
                return $"手打失败于砖块 #{hit.Floor} · {reason}";
            }
            if (PlaySession.Completed)
                return "手打完成";

            return _isPlaying ? "手打进行中" : "手打已暂停";
        }
    }

    public void SetManualPlay(bool enabled)
    {
        if (PlayInput.Enabled == enabled)
            return;

        Pause();
        ClearManualAttempt();
        PlayInput.Enabled = enabled;
        if (enabled)
            Play();
        else if (_imageDecorations?.HasConditions == true)
        {
            ResetDecorationTriggers();
            ApplyTimeline(automaticConditions: true);
        }
    }

    public void ResetManualPlay()
    {
        Restart();
        if (PlayInput.Enabled)
            Play();
    }

    private void ClearManualAttempt()
    {
        PlayInput.ReleaseAll();
        PlayInput.Clear();
        PlaySession.Clear();
        ResetCameraHit();
    }

    /// <summary>Rewind to the beginning.</summary>
    public void Restart()
    {
        if (_floors.Count == 0)
        {
            return;
        }

        SetRuntimeTrackMode(false);
        _trackRuntime?.Reset();
        _isPlaying = false;
        _filterClockPaused = false;
        _playTime = 0.0;
        ResetDecorationTriggers();
        _lastFilterTime = double.NaN;
        _colorHistory?.Reset();
        _audioNeedsResync = true;
        _music.Stop();
        _hitsound.Stop();
        ClearManualAttempt();

        // Rewind: put the camera straight back
        _cameraSnap = true;
        _cameraFree = false;
        _camera.Zoom = PlaybackBaseZoom;
        _playbackZoomNeedsReset = true;
        ResetTrail();
        ApplyTimeline();
    }

    internal void SetProgressScrubbing(bool scrubbing)
    {
        if (_progressScrubbing == scrubbing)
            return;
        _progressScrubbing = scrubbing;
        _audioNeedsResync = true;
        if (scrubbing)
        {
            SetRuntimeTrackMode(true);
            _music.Pause();
            _hitsound.Pause();
            PlayInput.Clear();
        }
    }

    /// <summary>Seek to a specific time.</summary>
    public void Seek(double seconds)
    {
        if (_floors.Count == 0)
        {
            return;
        }

        _colorHistory?.Reset();
        _lastFilterTime = double.NaN;
        double target = Math.Clamp(seconds, 0.0, _duration);
        if (target == 0.0 && !_progressScrubbing)
        {
            Restart();
            return;
        }

        bool rewindsJudgments = target < _playTime && _lastDecorationConditionFloor > FloorAtTime(_entryTimes, target);
        if (rewindsJudgments || PlayInput.Enabled || _imageDecorations?.HasTriggeredCollision == true)
            ResetDecorationTriggers();
        else
            ResetDecorationContacts();
        _playTime = target;
        if (PlayInput.Enabled && _manualTrack is { LastFloor: >= 1 } track)
        {
            ClearManualAttempt();
            int start = FloorAtTime(_entryTimes, target);
            PlaySession.Start(track, start, target);
            if (start > 0)
                _imageDecorations?.TriggerCondition(start, HitMargin.Perfect, _playTime, checkpoint: true);
        }

        // Scrub: snap the camera onto the target tile instead of sliding across the whole level
        _cameraSnap = true;
        _cameraFree = false;
        ResetTrail();
        ApplyTimeline(previewCameraEvents: true, automaticConditions: true);

        // Sync both clips once at the final position on the next tick.
        _audioNeedsResync = true;
    }

    /// <summary>
    /// Advance playback. The clock accumulates wall time and only nudges proportionally on a persistent
    /// offset; it never snaps to the audio position, which is quantised per buffer and would make the
    /// trail's sampling stutter.
    /// </summary>
    private void UpdatePlayback(double delta)
    {
        if (_floors.Count == 0 || _progressScrubbing)
        {
            return;
        }

        bool playing = _isPlaying;
        bool manual = ManualAttemptActive;
        if (playing && manual)
        {
            PlaySession.Advance(AdvancePlaybackClock(delta), _lastFrameTime - delta, _lastFrameTime,
                PlayInput.Pending, PlayInput.MarginMultiplier, _cameraHitCallback ??= OnPlaybackCameraHit);
            PlayInput.Clear();
            _playTime = PlaySession.Time;
            if (PlaySession.Failed || PlaySession.Completed)
                Pause();
        }
        else if (_isPlaying)
        {
            _playTime = Math.Min(AdvancePlaybackClock(delta), _duration);
            if (_playTime >= _duration)
                _isPlaying = false;
        }
        else
        {
            PlayInput.Clear();
        }

        ApplyTimeline(automaticConditions: playing);
        if (playing)
            UpdateDecorationTriggers(manual);
    }

    /// <summary>
    /// Resolve the playback clock: advance by <paramref name="delta"/>, then absorb any audio offset
    /// proportionally. A large offset (a seek or a resume) is applied directly instead.
    /// </summary>
    private double AdvancePlaybackClock(double delta)
    {
        double next = _playTime + delta * _pitch;
        if (_audioNeedsResync)
            return next;

        AudioClip? master = null;
        double masterShift = 0.0;

        if (_musicEnabled && _music.IsLoaded && _music.IsPlaying)
        {
            master = _music;
            masterShift = AudioClockShift;
        }
        else if (_hitsoundEnabled && _hitsound.IsLoaded && _hitsound.IsPlaying)
        {
            master = _hitsound;
            masterShift = HitsoundClockShift;
        }

        if (master is null)
        {
            return next;
        }

        if (!TryGetAudioClock(master, masterShift, out double audioTime))
        {
            // Right after starting, or while the demuxer catches up, the position is not
            // meaningful; advance on the wall clock alone
            return next;
        }

        double drift = audioTime - next;

        // An offset the size of a real jump: snap, a soft correction would take far too long
        if (Math.Abs(drift) >= PlaybackClockSnapSeconds)
        {
            return audioTime;
        }

        // Proportional correction: absorb a small fraction per frame, with a time constant far
        // longer than one frame
        if (Math.Abs(drift) > PlaybackClockDeadZoneSeconds && delta > 0.0)
        {
            next += drift * Math.Min(1.0, delta / PlaybackClockCorrectionSeconds);
        }

        return next;
    }

    /// <summary>
    /// Reads the clip's usable clock. Just after a start the position can still be 0 or far behind the
    /// current progress; such a sample is untrustworthy, so return false and let the caller fall back
    /// to the wall clock.
    /// </summary>
    private bool TryGetAudioClock(AudioClip clip, double shift, out double seconds)
    {
        double sourceTime = clip.Position * _pitch;
        seconds = sourceTime - shift;

        if (!(sourceTime > 0.0) || !double.IsFinite(seconds))
        {
            return false;
        }

        // Falling this far behind means the queue is still filling; ignore this frame
        return seconds + PlaybackClockDeadZoneSeconds >= _playTime;
    }

    /// <summary>
    /// Evaluate the current floor, planet angle and camera position from _playTime.
    /// _playTime is the **music file position** clock, which the music and hitsound also follow.
    /// </summary>
    private void ApplyTimeline(bool previewCameraEvents = false, bool automaticConditions = false)
    {
        if (_floors.Count == 0)
        {
            _currentIndex = 0;
            _rotationAngle = 0f;
            _cameraShake = Vector2.Zero;
            return;
        }

        // A floor may be entered before the music file starts. Keep that signed timing so its
        // sweep is already in progress when playback reaches zero.
        bool manual = ManualAttemptActive;
        int index = manual && _floors.Count > 1 ? PlaySession.CurrentFloor : FloorAtTime(_entryTimes, _playTime);
        if (automaticConditions && !manual)
            UpdateAutomaticConditions(index);
        bool useCameraEffects = _runtimeTrackActive || previewCameraEvents;
        CameraTimeline? cameraEvents = useCameraEffects ? _cameraTimeline : null;
        _cameraShake = useCameraEffects && _sceneEffects is { } effects
            ? effects.ShakeAt(_playTime) : Vector2.Zero;

        // A tile change alone does not create a seam: at normal speed the secondary lands where the
        // primary used to be, so the path is continuous. Seams come from the jump check below.
        _currentIndex = index;
        _isCw = _floors[index].isCW;
        PlanetPose pose = EvaluatePlanetPose(index, _playTime, manual);
        _planetCenter = pose.Pivot;
        _secondaryCenter = pose.Orbit;
        _thirdCenter = pose.Third;
        _rotationAngleContinuous = (float)pose.ContinuousAngle;
        _rotationAngle = _floors.Count == 1 ? (float)pose.Angle : WrapAngle(pose.Angle);
        UpdatePlaybackCamera(index, pose.Pivot, cameraEvents);
    }

    private readonly record struct PlanetPose(Vector2 Pivot, Vector2 Orbit, Vector2 Third,
        double Angle, double ContinuousAngle);

    private PlanetPose EvaluatePlanetPose(int index, double time, bool manual)
    {
        if (_runtimeTrackActive)
            _trackRuntime?.Advance(time);

        (Vector2 here, float floorRotation) = PlanetAnchor(index, time);
        float trackRotation = MathHelper.RadiansToDegrees(floorRotation);
        PlanetTrackFloor track = _planetTrack[index];
        float radius = track.Radius * TileSpacing;
        bool clockwise = _floors[index].isCW;
        int last = _floors.Count - 1;
        if (last == 0)
            return new PlanetPose(here, here + PlanetTrackFloor.Direction(trackRotation) * radius,
                here + PlanetTrackFloor.Direction(trackRotation + PlanetTrackFloor.ThirdAngle(clockwise)) * radius,
                trackRotation, trackRotation);

        // Window per tile: floor 0 starts at playback time 0 (spinning in place), the rest start when
        // the planet lands on that tile. The final tile has no next entry time, so its window is
        // derived from its own sweep, and playback past the end keeps the planet turning instead of
        // clamping t at 1.
        double fromTime = index > 0 || manual ? _entryTimes[index] : 0.0;
        double toTime = index < last
            ? _entryTimes[index + 1]
            : _entryTimes[index] + LastFloorWindow(index);
        double span = toTime - fromTime;

        // Only intermediate tiles clamp to [0,1]; the first tile (countdown wait) and the last tile
        // (keeps turning after the end) do not clamp the upper bound.
        double t = manual && PlaySession.Completed ? 0.0
            : span > 1e-9 ? (time - fromTime) / span : 0.0;

        if (index > 0 && index < last)
        {
            t = Math.Clamp(t, 0.0, 1.0);
        }
        else
        {
            t = Math.Max(t, 0.0);
        }

        double eased = EasePlanet(index, t);
        double rawDegrees = _sweepStart[index] + _sweepDelta[index] * t;
        double degrees = _sweepStart[index] + _sweepDelta[index] * eased;
        if (manual && index < last && _manualTrack is { } trackTiming)
        {
            double direction = clockwise ? -1.0 : 1.0;
            rawDegrees = _sweepStart[index] + _sweepDelta[index]
                + (time - toTime) * trackTiming.AngleRate(index) * direction;
            degrees = eased == t ? rawDegrees : degrees;
        }
        double continuousAngle = (manual ? rawDegrees : degrees) + trackRotation;
        degrees += trackRotation;
        if (_pauseAngles?.TryGetValue(index, out PauseAngleCorrection correction) == true)
            degrees += correction.At(time - fromTime);

        if (index < last)
        {
            float progress = (float)Math.Clamp(eased, 0, 1);
            float landingRadius = _planetTrack[index + 1].Radius * TileSpacing;
            if (track.HasHold)
            {
                here = PlanetTrackFloor.HoldPivot(here, PlanetAnchor(index + 1, time).Center, _floors[index].angle,
                    landingRadius, progress);
            }
            radius += (landingRadius - radius) * progress;
        }
        return new PlanetPose(here, here + PlanetTrackFloor.Direction(degrees) * radius,
            here + PlanetTrackFloor.Direction(degrees + PlanetTrackFloor.ThirdAngle(clockwise)) * radius,
            degrees, continuousAngle);
    }

    private void ResetCameraHit()
    {
        _cameraPulseTime = double.NegativeInfinity;
        _cameraPulseFloor = -1;
        _cameraHoldOffset = Vector2.Zero;
        _cameraHoldReleaseTime = double.NaN;
    }

    private void OnPlaybackCameraHit(HitRecord hit)
    {
        if (hit.Accepted && hit.Floor > 0 && hit.Floor != _cameraPulseFloor)
        {
            _cameraPulseFloor = hit.Floor;
            _cameraPulseTime = hit.Time;
        }
        if (_imageDecorations?.HasConditions == true)
            TriggerDecorationJudgment(hit);
    }

    private void UpdatePlaybackCamera(int index, Vector2 here, CameraTimeline? cameraEvents)
    {
        bool useEvents = cameraEvents is not null;
        _cameraRenderDisabled = !_cameraFree && _settings.CameraZoom == 0f;
        if (useEvents != _cameraEventActive)
        {
            _cameraSnap = true;
            _cameraEventActive = useEvents;
            if (!useEvents)
            {
                if (!_cameraFree)
                {
                    _camera.Rotation = _homeRotation;
                    if (_cameraEventZoomApplied)
                        _camera.Zoom = PlaybackBaseZoom;
                }
                _cameraEventZoomApplied = false;
            }
        }

        Vector2 anchor = PlanetAnchor(index).Center;
        Vector2 target = useEvents ? anchor : LevelCameraTarget(anchor);
        Vector2 holdOffset = here - anchor;
        if (!useEvents && _settings.CameraRelativeTo != SharpFAI.Serialization.EventEnums.CamMovementType.Player)
            holdOffset = Vector2.Zero;
        if (ManualAttemptActive && PlaySession.ReleasedHoldEarly)
        {
            if (double.IsNaN(_cameraHoldReleaseTime))
            {
                _cameraHoldReleaseOffset = _cameraHoldOffset;
                _cameraHoldReleaseTime = _lastFrameTime;
                _cameraFrom = useEvents ? _cameraFollowCenter : _camera.Position;
                _cameraElapsed = 0;
                if (index > 0)
                    _cameraTo = useEvents ? PlanetAnchor(index - 1).Center : LevelCameraTarget(PlanetAnchor(index - 1).Center);
            }
            holdOffset = CameraMotion.ReleaseHold(_cameraHoldReleaseOffset, _lastFrameTime - _cameraHoldReleaseTime);
        }
        else
        {
            _cameraHoldReleaseTime = double.NaN;
        }
        _cameraHoldOffset = holdOffset;

        // Advance the tween with the playback clock so a paused player freezes the camera too
        double step = _playTime - _lastCameraTime;
        _lastCameraTime = _playTime;

        if (_cameraSnap || step < 0.0)
        {
            // Seek or restart: snap into place instead of sliding across the level. Triggered only by an
            // explicit Seek/Restart; a frame hitch (GC etc.) is not a jump and is caught up by the
            // accumulation below.
            _cameraSnap = false;
            _cameraIndex = index;
            _cameraFrom = target + holdOffset;
            _cameraTo = target;
            _cameraElapsed = 0.0;
            if (!ManualAttemptActive)
            {
                _cameraPulseTime = double.NegativeInfinity;
                _cameraPulseFloor = -1;
            }
        }
        else
        {
            if (index != _cameraIndex)
            {
                _cameraIndex = index;
                _cameraFrom = useEvents ? _cameraFollowCenter : _camera.Position;
                _cameraTo = target;
                double landedAt = ManualAttemptActive ? _cameraPulseTime : _entryTimes[index];
                _cameraElapsed = double.IsFinite(landedAt) ? Math.Max(0, _playTime - landedAt) : 0;
            }
            else
            {
                _cameraElapsed += Math.Max(0, step);
            }
        }

        // The planet sits on its real tile; only the camera interpolates.
        // Skip the write-back once the user has taken manual control, otherwise every frame would
        // yank the camera back onto the tile.
        if (!_cameraFree)
        {
            Vector2 center = CameraMotion.Follow(_cameraFrom, _cameraTo, holdOffset, _cameraElapsed, _floors[index].bpm);
            _camera.Position = center;
            if (cameraEvents is not null)
            {
                _cameraFollowCenter = center;
                float baseZoom = _offlineSubSample ? GetExportZoom() : PlaybackBaseZoom;
                float basePercent = _camera.ViewportHeight / (MathF.Max(baseZoom, 1e-4f) * 10f);
                CameraTimeline.Sample sample = ManualAttemptActive
                    ? cameraEvents.Evaluate(_playTime, center, basePercent)
                    : cameraEvents.Evaluate(_playTime, basePercent);
                _camera.Position = sample.Position;
                _camera.Rotation = sample.Rotation;
                _cameraRenderDisabled = sample.Zoom == 0f;
                if (sample.Zoom is float zoom && zoom != 0f)
                    _camera.Zoom = AuthoredZoom(_camera.ViewportHeight, zoom);
                else if (_cameraEventZoomApplied)
                    _camera.Zoom = baseZoom;
                _cameraEventZoomApplied = sample.Zoom.HasValue;
            }
            _camera.MarkDirty();
        }
    }

    /// <summary>
    /// 是否让经过的砖块消失。为 true 时星球离开一块砖后该砖立刻隐藏。
    /// Whether tiles vanish once the planet has left them. When true a tile disappears the moment the
    /// planet moves on.
    /// </summary>
    /// <remarks>
    /// 官方的消失延迟来自 <c>AnimateTrack</c> 的 <c>beatsBehind</c>；这里额外提供强制开关，
    /// 方便录像时只看当前砖块与行星。开启后所有砖块的延迟按 0 处理。
    /// The game's delay comes from `AnimateTrack`'s `beatsBehind`; this is an override so a recording
    /// can show only the current tile and the planet. While on, every delay is treated as zero.
    /// </remarks>
    public bool HidePassedFloors
    {
        get => _hidePassedFloors;
        set
        {
            if (_hidePassedFloors == value)
            {
                return;
            }

            _hidePassedFloors = value;
            _floorVisualTime = double.NaN;
        }
    }

    /// <summary>
    /// Window length for the final tile, derived from its sweep angle at 180 degrees per beat,
    /// so the planet still completes its remaining travel on the last tile.
    /// </summary>
    private double LastFloorWindow(int index)    {
        if (index <= 0 || index >= _floors.Count)
        {
            return 0.5;
        }

        double bpm = _floors[index].bpm > 1e-6 ? _floors[index].bpm : 120.0;
        double beat = 60.0 / bpm;
        double swept = Math.Abs(_sweepDelta[index]);

        // Sweep / 180 gives the number of beats; clamp to at least one beat so the window is never zero
        double beats = Math.Max(swept / 180.0, 1.0);
        return beat * beats;
    }

    /// <summary>
    /// 离线渲染：把播放时钟直接设到指定时刻并推进一帧，不做实时累加也不播放音频。
    /// Offline rendering: put the clock exactly at the given time and evaluate one frame, with no
    /// real-time accumulation and no audio.
    /// </summary>
    /// <remarks>
    /// 导出视频时用固定时间步逐帧求值，画面与音乐在同一个「文件位置时钟」上，
    /// 因此音画天然对齐，不受实时帧率抖动影响。
    /// Video export evaluates every frame on a fixed step. Both the picture and the music live on the
    /// same file-position clock, so they line up exactly regardless of real-time jitter.
    /// </remarks>
    /// <param name="seconds">目标播放时刻（秒，文件位置时钟）。</param>
    public void SeekForOfflineRender(double seconds)
    {
        if (IsLoading || _floors.Count == 0 || !double.IsFinite(seconds))
        {
            return;
        }

        // 离线导出必须由时间轴驱动相机：用户之前的平移/缩放留下的「相机自由」标志
        // 会让相机彻底不动，导出画面就成了死镜头。
        // Offline export must let the timeline drive the camera: a leftover "camera is free" flag
        // from earlier panning or zooming freezes the camera and the export becomes a static shot.
        _cameraFree = false;

        bool offlineStart = !_offlineSubSample;
        if (offlineStart)
        {
            _colorHistory?.Reset();
            _filterBackend?.ResetCameraHistory();
            _lastFilterTime = double.NaN;
            _runtimeTrackBeforeOffline = _runtimeTrackActive;
            SetRuntimeTrackMode(true);
            _offlineSubSample = true;
            _floorVisualTime = double.NaN;
            ResetTrail();
            ResetDecorationTriggers();
        }

        // 逐帧顺序推进，所以相机补间拿到的步长正好是一帧；首帧从 0 开始不会跳跃
        // Frames are submitted in order, so the camera tween sees exactly one frame of step
        _playTime = Math.Clamp(seconds, 0.0, Math.Max(_duration, seconds));
        _isPlaying = false;

        UpdateAutomaticConditions(FloorAtTime(_entryTimes, _playTime));
        UpdateOfflineTrail(_playTime);
        ApplyTimeline();
        UpdateDecorationTriggers(manual: false);
    }
}
