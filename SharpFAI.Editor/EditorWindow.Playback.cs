using System.Diagnostics;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Editor.Widgets;
using SharpFAI.ModApi;
using SharpFAI.Player;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private enum PlaybackView { Edit, Preview, Auto, Manual }
    private enum AuditionMode { Mixed, MusicOnly, HitsoundsOnly }
    private readonly record struct PlaybackRevision(EditorDocument Document, int Version, string SourcePath, double Speed, int ModGeneration);
    private readonly record struct EditSelection(int Floor, bool HasFloor, int? RangeAnchor, int EventPage,
        bool InspectDecoration, JObject? Decoration, JObject[] Decorations);

    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
    private long _documentLoadStarted = Stopwatch.GetTimestamp();
    private double _documentLoadSeconds;
    internal long InitialDocumentLoadStarted { init => _documentLoadStarted = value; }
    private readonly PlaybackPerformanceWindow _playbackPerformance = new(L.T);
    private LevelRenderer? _playback;
    private PlaybackView _playbackView;
    private PlaybackRevision? _requestedRevision;
    private PlaybackRevision? _displayedRevision;
    private PlaybackRevision? _observedRevision;
    private PlaybackRevision? _pendingRevision;
    private PlaybackRevision? _failedRevision;
    private bool _pendingEffects;
    private double _lastPreviewEdit;
    private bool _startPlayback;
    private PlaybackRate _playbackRate;
    private bool _manualPlay;
    private bool _noFail;
    private bool _tilesOnly;
    private int _playbackSpeed = 50;
    private bool _metronomeEnabled;
    private LevelRenderer.PlaybackTimeline? _metronomeTiming;
    private EditorTimeSignatures? _metronomeSignatures;
    private AuditionMode _auditionMode;
    private int _auditionVolume = 100;
    private double _wheelSpeedRemainder;
    private bool _wheelSpeedFine;
    private readonly List<(float Delta, bool Fine)> _speedWheels = [];
    private double _showSpeedUntil;
    private double _appliedPlaybackSpeed = 1;
    private int? _playbackFloor;
    private EditSelection? _savedEditSelection;
    private (Vector2 Position, float Zoom)? _savedEditCamera;
    private bool _restoringEditViewport;
    private bool _playbackHovered;
    private bool _playbackPan;
    private bool _playbackFrameAvailable;
    private bool _playbackFramePending;
    private int _playbackFbo;
    private int _playbackTexture;
    private int _playbackWidth;
    private int _playbackHeight;

    private PlaybackRevision CurrentPlaybackRevision => new(_document, _document.PlaybackVersion,
        _document.PlaybackPath, _appliedPlaybackSpeed, _modUi?.Host.LoadGeneration ?? 0);
    private bool HasCurrentPlayback => _playback is { IsLoading: false, LoadError: null, Level: not null }
                                      && _displayedRevision == CurrentPlaybackRevision;
    private PlaybackView SelectedPlaybackMode => _manualPlay ? PlaybackView.Manual : PlaybackView.Auto;
    private bool PlaybackCanPause => _playbackView == PlaybackView.Auto ||
        _playbackView == PlaybackView.Manual && (_startPlayback || _playback?.IsPlaying == true);

    private static void InitializePlayback() => Shader.SourceResolver = ShaderLibrary.ResolveForEngine;

    private void SwitchPlaybackView(PlaybackView view, PlaybackRate rate = PlaybackRate.Normal, int? startFloor = null)
    {
        double multiplier = EditorCanvasInput.PlaybackMultiplier(_playbackSpeed, rate);
        if (view is PlaybackView.Auto or PlaybackView.Manual &&
            !EditorCanvasInput.TryPlaybackPitch(Number(_document.Settings["pitch"], 100), multiplier, out _))
        {
            ShowError(new InvalidOperationException(L.IsChinese ? "播放速度与谱面音调的乘积超出音频后端范围。" :
                "Playback speed combined with chart pitch exceeds the audio backend's supported range."));
            return;
        }
        PlaybackView previous = _playbackView;
        EndTimelineRangePreview();
        StopPlaybackLoop();
        _playbackView = view;
        if (view != PlaybackView.Edit) _playback?.ClearEditorInsertedTrack();
        _startPlayback = view is PlaybackView.Auto or PlaybackView.Manual;
        if (_startPlayback)
        {
            _playbackRate = rate;
            _appliedPlaybackSpeed = multiplier;
        }
        else if (view == PlaybackView.Edit)
            _appliedPlaybackSpeed = 1;
        _playbackHovered = false;
        ResetCanvasGesture();

        if (view == PlaybackView.Edit)
        {
            if (_savedEditSelection is { } selection)
            {
                _savedEditSelection = null;
                _floor = selection.Floor;
                _hasFloorSelection = selection.HasFloor;
                _rangeAnchor = selection.RangeAnchor;
                _eventPage = selection.EventPage;
                _inspectDecoration = selection.InspectDecoration;
                _decoration = selection.Decoration;
                _selectedDecorations.Clear();
                _selectedDecorations.UnionWith(selection.Decorations);
                ClampSelection();
                _centerEditDecoration = null;
            }
            _playbackFloor = _floor;
            _centerEditFloor = null;
            _playback?.Pause();
            _playback?.SetManualPlay(false);
            _playback?.SetEditorTilesOnly(_tilesOnly);
            if (_savedEditCamera is { } camera && _playback is { Level: not null } renderer &&
                _displayedRevision?.Document == _document)
            {
                RestoreEditCamera(renderer, camera);
                _restoringEditViewport |= previous != PlaybackView.Edit;
            }
            return;
        }

        _playback?.SetEditorTilesOnly(false);
        _restoringEditViewport = false;
        if (previous == PlaybackView.Edit)
        {
            _savedEditSelection = new(_floor, _hasFloorSelection, _rangeAnchor, _eventPage, _inspectDecoration,
                _decoration, _selectedDecorations.ToArray());
            if (_playback is { Level: not null } editRenderer)
                _savedEditCamera = (editRenderer.Camera.Position, editRenderer.Camera.Zoom);
        }
        if (_startPlayback && startFloor is int floor)
            _playbackFloor = floor;
        else if (_startPlayback && rate == PlaybackRate.Configured && (!_hasFloorSelection || _inspectDecoration))
            _playbackFloor = 0;
        else if (previous == PlaybackView.Edit || view == PlaybackView.Preview)
            _playbackFloor = _floor;
        else if (view == PlaybackView.Manual && _playbackFloor is null && HasCurrentPlayback)
            _playbackFloor = _playback!.CurrentIndex;

        if (_playback is { LoadError: not null })
            _requestedRevision = null;
        _failedRevision = null;
        if (view == PlaybackView.Preview)
        {
            _playback?.Pause();
            _playback?.SetManualPlay(false);
        }
    }

    private void PauseToPreview()
    {
        StopPlaybackLoop();
        _startPlayback = false;
        _playback?.Pause();
        _playback?.SetManualPlay(false);
        _playbackView = PlaybackView.Preview;
    }

    private void StopPlaybackLoop()
    {
        _loopingSelection = null;
        if (_playback is { } renderer)
            renderer.PlaybackLoop = null;
    }

    private void SeekPerformanceSample(PlaybackPerformance.Sample sample)
    {
        if (!HasCurrentPlayback || _playback is not { } renderer)
            return;
        bool editing = _playbackView == PlaybackView.Edit;
        SeekTimeline(renderer, sample.Time, editing, -1);
        if (!editing)
        {
            SelectFloor(sample.Floor, false);
            _playbackFloor = null;
        }
    }

    private void UpdatePlayback()
    {
        double now = _playbackClock.Elapsed.TotalSeconds;
        bool editing = _playbackView == PlaybackView.Edit;
        if (_playback is null)
        {
            _playback = new LevelRenderer { Mods = _modUi?.Host, ModTarget = UiHost.Editor };
            ApplyPreviewSettings(_playback);
        }
        LevelRenderer renderer = _playback;
        var previousLevel = renderer.Level;
        bool hadEditScene = editing && previousLevel is not null && _displayedRevision?.Document == _document;
        if (hadEditScene && renderer.IsLoading)
            _savedEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
        renderer.Tick(now);
        if (editing && renderer.Level is not null && !ReferenceEquals(previousLevel, renderer.Level) &&
            _requestedRevision?.Document == _document)
        {
            if (_savedEditCamera is { } camera)
                RestoreEditCamera(renderer, camera);
            _playbackFloor ??= _floor;
        }
        PlaybackRevision target = CurrentPlaybackRevision;
        if (_observedRevision != target)
        {
            _observedRevision = target;
            _lastPreviewEdit = now;
        }
        if (renderer.Level is not null && !ReferenceEquals(previousLevel, renderer.Level) && _requestedRevision is { } loaded)
            AcceptPlaybackRevision(loaded);

        bool effectsFailed = false;
        if (_pendingRevision is { } pending)
        {
            if (!SamePlaybackSource(pending, target))
            {
                renderer.CancelEditorEffectUpdate();
                renderer.CancelEditorRevision();
            }
            else
            {
                if (!_pendingEffects && pending != target &&
                    (_document.HasOnlyTileEditsSince(pending.Version) || _document.HasOnlyEventAppendsSince(pending.Version)))
                    renderer.RequestEditorRevisionCancellation();
                if (!FinishPlaybackRevision(renderer, pending, target, _pendingEffects, out effectsFailed))
                    return;
            }
            _pendingRevision = null;
        }

        if (_displayedRevision != target)
        {
            if (_failedRevision == target) return;
            if (renderer.CanUpdateModEditorRevision &&
                !renderer.IsLoading && renderer.Level is not null && _displayedRevision is { } current &&
                SamePlaybackSource(current, target))
            {
                try
                {
                    bool effects = !renderer.HasModIncrements && !effectsFailed && current.Speed == target.Speed &&
                        renderer.BeginEditorEffectUpdate(_document.EffectChangesSince(current.Version));
                    if (effects && !FinishPlaybackRevision(renderer, target, target, effects: true, out effectsFailed))
                        return;
                    if (_displayedRevision == target) goto PlaybackReady;

                    bool tileEdit = _document.HasOnlyTileEditsSince(current.Version);
                    bool nearEnd = _document.LastFloor - _floor < 64;
                    bool tailEdit = nearEnd && (tileEdit || _document.HasOnlyEventAppendsSince(current.Version));
                    double delay = 0.12;
                    if (editing && tileEdit && _document.IsLargeChart && !nearEnd) delay = 0.25;
                    else if (editing && (tailEdit || tileEdit && !_document.IsLargeChart)) delay = 0.035;
                    if (!effectsFailed && !_startPlayback && !_document.CanReusePlaybackChart(current.Version) &&
                        now - _lastPreviewEdit < delay) return;

                    var settings = (JObject)_document.Settings.DeepClone();
                    if (target.Speed != 1) settings["pitch"] = Number(settings["pitch"], 100) * target.Speed;
                    var patch = _document.PlaybackPatchSince(current.Version);
                    var snapshot = patch is not null || _document.CanReusePlaybackChart(current.Version)
                        ? null : _document.SnapshotForPlayback();
                    if (!renderer.BeginEditorRevision(snapshot, settings, patch))
                        throw new InvalidOperationException("The live renderer is not ready to accept editor revisions.");
                    if (!FinishPlaybackRevision(renderer, target, target, effects: false, out _) || _displayedRevision != target)
                        return;
                }
                catch (Exception error)
                {
                    _failedRevision = target;
                    ShowError(error);
                    return;
                }
            }
            else
            {
                if (_requestedRevision is { } request && (request == target ||
                    (renderer.IsLoading && SamePlaybackSource(request, target)))) return;
                _playbackFloor ??= _floor;
                if (hadEditScene) _savedEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
                var snapshot = _document.SnapshotForPlayback();
                if (target.Speed != 1)
                    snapshot.PutSetting("pitch", Number(_document.Settings["pitch"], 100) * target.Speed);
                renderer.Load(snapshot, target.SourcePath, keepEditorScene: hadEditScene);
                _requestedRevision = target;
                return;
            }
        }

    PlaybackReady:
        if (!HasCurrentPlayback)
            return;
        UpdateEditorMetronome(renderer);
        if (_documentLoadStarted != 0)
        {
            _documentLoadSeconds = Stopwatch.GetElapsedTime(_documentLoadStarted).TotalSeconds;
            _documentLoadStarted = 0;
        }

        if (_playbackFloor is int requestedFloor)
        {
            int last = renderer.FloorCount - (_startPlayback ? 2 : 1);
            if (renderer.IsPlaying)
                renderer.Pause();
            int floor = Math.Clamp(requestedFloor, 0, Math.Max(0, last));
            if (editing)
                SeekEditFloor(renderer, floor);
            else
                renderer.SelectFloor(floor);
            _playbackFloor = null;
        }

        if (editing)
            return;

        if (_startPlayback)
        {
            _startPlayback = false;
            bool manual = _playbackView == PlaybackView.Manual;
            renderer.PlaySession.NoFail = _noFail;
            renderer.SetManualPlay(manual);
            if (_loopingSelection is { } loop)
            {
                renderer.PlaybackLoop = loop;
                renderer.Seek(loop.Start);
            }
            if (!renderer.IsPlaying)
                renderer.Play();
        }
        else if (_playbackView == PlaybackView.Auto && !renderer.IsPlaying)
        {
            StopPlaybackLoop();
            _playbackView = PlaybackView.Preview;
        }
    }

    private void FeedEditorWheel(ImGuiIO* io, float x, float y, bool ctrl, bool shift)
    {
        // Keep modifiers tied to the event; key-up and wheel can reach the same frame.
        if (ctrl && y != 0)
            _speedWheels.Add((y, shift));
        else
            ImGuiIO.AddMouseWheelEvent(io, x, y);
    }

    private bool CanAdjustPlaybackSpeed => _playbackView is PlaybackView.Edit or PlaybackView.Preview &&
        ImGui.GetIO()->WantTextInput == 0 && ImGui.IsAnyItemActive() == 0 && _pending is null && _error is null &&
        ImGui.IsPopupOpen((string?)null, (int)(ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupId |
            ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupLevel)) == 0;

    private void ApplySpeedWheel()
    {
        if (_speedWheels.Count == 0 || !CanAdjustPlaybackSpeed || ImGui.IsWindowHovered() == 0) return;
        foreach (var (delta, fine) in _speedWheels)
        {
            if (fine != _wheelSpeedFine)
            {
                _wheelSpeedFine = fine;
                _wheelSpeedRemainder = 0;
            }
            _playbackSpeed = EditorCanvasInput.WheelSpeed(_playbackSpeed, delta, fine, ref _wheelSpeedRemainder);
        }
        _speedWheels.Clear();
        _showSpeedUntil = _playbackClock.Elapsed.TotalSeconds + .65;
    }

    private bool ShowSpeedIndicator => CanAdjustPlaybackSpeed &&
        (ImGui.GetIO()->KeyCtrl != 0 || _playbackClock.Elapsed.TotalSeconds < _showSpeedUntil);

    private void DrawSpeedIndicator(ImVec2 min, ImVec2 max)
    {
        if (!ShowSpeedIndicator) return;
        string text = $"{_playbackSpeed}%";
        ImVec2 size = ImGui.CalcTextSize(text);
        if (max.x - min.x < size.x + 24 || max.y - min.y < size.y + 20) return;
        ImVec2 end = WidgetDraw.V2(max.x - 8, max.y - 8);
        ImVec2 start = WidgetDraw.V2(end.x - size.x - 14, end.y - size.y - 8);
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImDrawList.AddRectFilled(draw, start, end, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_PopupBg, .95f), 4);
        ImDrawList.AddText(draw, WidgetDraw.V2(start.x + 7, start.y + 4), WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_CheckMark), text);
    }

    private static PlaybackRate HeldPlaybackRate => ImGui.GetIO()->KeyCtrl != 0 ? PlaybackRate.Configured : PlaybackRate.Normal;

    private void DrawPlaybackSpeed()
    {
        int speed = _playbackSpeed;
        if (ImGui.DragInt("##playback-speed", ref speed, 1, 1, 1000, "%d%%",
                (int)ImGuiSliderFlags.ImGuiSliderFlags_AlwaysClamp) != 0)
        {
            _playbackSpeed = Math.Clamp(speed, 1, 1000);
            _wheelSpeedRemainder = 0;
        }
        WidgetDraw.ItemTooltip(L.T("PlaybackSpeedHelp"));
    }

    private void UpdateEditorMetronome(LevelRenderer renderer)
    {
        var timing = _metronomeEnabled ? renderer.EditorTiming : null;
        var signatures = timing is null ? null : _document.TimeSignatures;
        if (ReferenceEquals(timing, _metronomeTiming) && ReferenceEquals(signatures, _metronomeSignatures))
            return;
        _metronomeTiming = timing;
        _metronomeSignatures = signatures;
        if (timing is null || signatures is null)
        {
            renderer.SetEditorMetronome(null);
            return;
        }

        var clock = new EditorBeatClock(timing.EntryTimes, timing.SnapBpm);
        renderer.SetEditorMetronome(new EditorMetronome(clock, signatures).Render);
    }

    private void DrawQuickAudition()
    {
        const string popup = "##quick-audition-options";
        if (EditorControls.Action("quick-audition", EditorControls.ControlIcon.Headphones,
                emphasized: _auditionMode != AuditionMode.Mixed || _auditionVolume != 100))
            ImGui.OpenPopup(popup, 0);
        WidgetDraw.ItemTooltip(L.T("QuickAudition") + " · " + AuditionLabel(_auditionMode));
        if (ImGui.BeginPopup(popup) == 0) return;

        ImGui.TextUnformatted(L.T("QuickAudition"));
        ImGui.Separator();
        AuditionMode mode = _auditionMode;
        ReadOnlySpan<AuditionMode> options = [AuditionMode.Mixed, AuditionMode.MusicOnly, AuditionMode.HitsoundsOnly];
        foreach (AuditionMode option in options)
            if (ImGui.RadioButton(AuditionLabel(option), (byte)(mode == option ? 1 : 0)) != 0)
                mode = option;

        ImGui.Separator();
        ImGui.TextUnformatted(L.T("AuditionVolume"));
        ImGui.SetNextItemWidth(Math.Max(150 * _uiScale, ImGui.CalcTextSize(L.T("AuditionVolume")).x));
        int volume = _auditionVolume;
        ImGui.SliderInt("##audition-volume", ref volume, 0, 100, "%d%%",
            (int)ImGuiSliderFlags.ImGuiSliderFlags_AlwaysClamp);
        if (mode != _auditionMode || volume != _auditionVolume)
        {
            _auditionMode = mode;
            _auditionVolume = volume;
            if (_playback is { } renderer)
                ApplyPreviewAudioVolumes(renderer);
        }
        ImGui.EndPopup();
    }

    private static string AuditionLabel(AuditionMode mode) => L.T(mode switch
    {
        AuditionMode.MusicOnly => "AuditionMusicOnly",
        AuditionMode.HitsoundsOnly => "AuditionHitsoundsOnly",
        _ => "AuditionMixed"
    });

    private static bool SamePlaybackSource(PlaybackRevision first, PlaybackRevision second) =>
        first.Document == second.Document && first.SourcePath == second.SourcePath && first.ModGeneration == second.ModGeneration;

    private bool FinishPlaybackRevision(LevelRenderer renderer, PlaybackRevision pending, PlaybackRevision target,
        bool effects, out bool effectsFailed)
    {
        effectsFailed = false;
        LevelRenderer.EditorEffectStatus status;
        try
        {
            status = effects ? renderer.FinishEditorEffectUpdate(pending == target) : renderer.FinishEditorRevision(pending == target);
        }
        catch (Exception error)
        {
            _pendingRevision = null;
            _failedRevision = pending;
            ShowError(error);
            return true;
        }
        if (status == LevelRenderer.EditorEffectStatus.Pending)
        {
            _pendingRevision = pending;
            _pendingEffects = effects;
            return false;
        }
        _pendingRevision = null;
        if (status == LevelRenderer.EditorEffectStatus.Applied)
            AcceptPlaybackRevision(pending);
        else if (pending == target)
        {
            effectsFailed = effects;
            if (!effects)
            {
                _failedRevision = pending;
                ShowError(new InvalidOperationException(renderer.EditorRevisionError ?? L.T("Syncing")));
            }
        }
        return true;
    }

    private void AcceptPlaybackRevision(PlaybackRevision revision)
    {
        _requestedRevision = _displayedRevision = revision;
        if (revision.Document == _document)
            _pendingTiles.RemoveAll(tile => tile.Version <= revision.Version);
        revision.Document.ForgetEffectChangesThrough(revision.Version);
    }

    private void DrawPlaybackCanvas()
    {
        bool ready = HasCurrentPlayback;
        LevelRenderer? renderer = _playback;
        if (!_canvasOnly && !DrawPlaybackCanvasToolbar(ready, renderer))
            return;

        if (_showSceneView)
            DrawSceneView(editing: false);
        else
        {
            ImVec2 size = ImGui.GetContentRegionAvail();
            size.x = Math.Max(1, size.x);
            size.y = Math.Max(1, size.y);
            if (ready && renderer is not null)
                RenderPlaybackFrame(renderer, size);

            DrawPlaybackImage(size);
            _playbackHovered = ImGui.IsItemHovered() != 0;

            if (ready && renderer is not null && _playbackView == PlaybackView.Preview)
                NavigatePlaybackPreview(renderer, selectTiles: !_canvasOnly && !_playbackFramePending);
            if (ready && renderer is { CanNavigateEditorPlayback: true } && _playbackView is PlaybackView.Auto or PlaybackView.Manual)
                NavigateActivePlayback(renderer);
            else
                _playbackPan = false;
        }

        if (!_canvasOnly && ready && renderer is not null && _playbackView == PlaybackView.Manual)
        {
            ImVec2 min = ImGui.GetItemRectMin();
            ImVec2 max = ImGui.GetItemRectMax();
            ImDrawList* draw = ImGui.GetWindowDrawList();
            ImDrawList.PushClipRect(draw, min, max, 1);
            ManualPlayHud.Draw(renderer, draw, min, max, L.T);
            ImDrawList.PopClipRect(draw);
        }
    }

    private static string PlaybackLoadingStatus(LevelRenderer? renderer) => renderer?.LoadError ??
        (renderer?.IsLoading == true ? L.F("SyncStage", L.LoadPhase(renderer.LoadPhase)) : L.T("Syncing"));

    private bool DrawPlaybackCanvasToolbar(bool ready, LevelRenderer? renderer)
    {
        if (ImGui.Button(L.T("ReturnToEdit"), default) != 0)
        {
            ReturnToEdit();
            return false;
        }

        if (SceneViewAvailable)
        {
            string scene = _showSceneView ? "2D##playback-scene" : "3D##playback-scene";
            ContinueToolbar(ButtonWidth(scene));
            if (ImGui.Button(scene, default) != 0)
                SetSceneView(!_showSceneView);
            WidgetDraw.ItemTooltip(L.T(_showSceneView ? "SceneView2D" : "SceneView3D"));
        }
        if (!ready || renderer is null)
        {
            ToolbarReadout(PlaybackLoadingStatus(renderer));
        }
        else
        {
            ToolbarReadout(L.F("TileStatus", renderer.CurrentIndex, renderer.FloorCount - 1));
            ToolbarReadout(Timeline.FormatPosition(renderer.PlayTime), accent: true);
            if (_documentLoadSeconds > 0) ToolbarReadout(L.F("LoadCompletedTime", _documentLoadSeconds));
            if (renderer.CurrentFloorTempo is { } tempo)
            {
                string bpm = L.F("PlaybackTileBpm", tempo.Authored, tempo.Effective is double effective ? effective : "—");
                ToolbarReadout(bpm);
                WidgetDraw.ItemTooltip(L.T("PlaybackTileBpmHelp"));
            }
            if (_playbackView == PlaybackView.Manual)
            {
                bool noFail = _noFail;
                if (ToolbarToggle("NoFail", ref noFail))
                    SetNoFail(noFail);
            }
            else if (!_showTimeline)
            {
                ImGui.SetNextItemWidth(-1);
                float progress = (float)renderer.Progress;
                if (ImGui.SliderFloat("##player-progress", ref progress, 0, 1, "", 0) != 0)
                {
                    PauseToPreview();
                    renderer.Seek(progress * renderer.Duration);
                }
            }
        }

        return true;
    }

    private void DrawPlaybackImage(ImVec2 size)
    {
        if (_playbackFrameAvailable)
        {
            ImGui.Image(new ImTextureRef { _TexID = (uint)_playbackTexture }, size, WidgetDraw.V2(0, 1), WidgetDraw.V2(1, 0));
            if (!_canvasOnly && HasCurrentPlayback && _playback is { } renderer)
                _modUi?.DrawOverlay(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                    renderer, scene3D: false);
        }
        else
            ImGui.Dummy(size);
        if (!_canvasOnly)
            DrawSpeedIndicator(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
    }

    private void NavigatePlaybackPreview(LevelRenderer renderer, bool selectTiles)
    {
        ImGuiIO* io = ImGui.GetIO();
        ImVec2 origin = ImGui.GetItemRectMin();
        ImVec2 scale = io->DisplayFramebufferScale;
        Vector2 cursor = new((io->MousePos.x - origin.x) * scale.x, (io->MousePos.y - origin.y) * scale.y);
        Vector2 delta = new(io->MouseDelta.x * scale.x, io->MouseDelta.y * scale.y);
        var mouse = UpdateCanvasGesture(_playbackHovered);
        if (mouse == CanvasGesture.Drag)
            renderer.Pan(delta);
        else if (selectTiles && mouse == CanvasGesture.Click)
        {
            int floor = renderer.PickFloor(cursor);
            if (floor >= 0)
                SelectFloor(floor, true);
        }
        if (_playbackHovered && io->MouseWheel != 0)
            renderer.Zoom(io->MouseWheel, cursor);
    }

    private void NavigateActivePlayback(LevelRenderer renderer)
    {
        // Left and right clicks are gameplay inputs in manual mode.
        const int middle = (int)ImGuiMouseButton.ImGuiMouseButton_Middle;
        if (ImGui.IsMouseClicked(middle, 0) != 0)
            _playbackPan = _playbackHovered;
        if (ImGui.IsMouseDown(middle) == 0)
            _playbackPan = false;

        ImGuiIO* io = ImGui.GetIO();
        ImVec2 scale = io->DisplayFramebufferScale;
        Vector2 delta = _playbackPan && ImGui.IsMouseDragging(middle, 4) != 0
            ? new(io->MouseDelta.x * scale.x, io->MouseDelta.y * scale.y) : Vector2.Zero;
        float scroll = _playbackHovered ? io->MouseWheel : 0;
        if (delta != Vector2.Zero || scroll != 0)
        {
            ImVec2 origin = ImGui.GetItemRectMin();
            renderer.NavigateEditorPlayback(delta, scroll,
                new Vector2((io->MousePos.x - origin.x) * scale.x, (io->MousePos.y - origin.y) * scale.y));
        }
    }

    private void RenderPlaybackFrame(LevelRenderer renderer, ImVec2 size, SceneRenderView? scene = null)
    {
        _playbackFramePending = false;
        ImVec2 scale = ImGui.GetIO()->DisplayFramebufferScale;
        int width = Math.Max(1, (int)(size.x * scale.x));
        int height = Math.Max(1, (int)(size.y * scale.y));
        int previousDraw = GL.GetInteger(GetPName.DrawFramebufferBinding);
        int previousRead = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int* viewport = stackalloc int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        try
        {
            if (_playbackFbo == 0)
            {
                _playbackFbo = GL.GenFramebuffer();
                _playbackTexture = GL.GenTexture();
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _playbackFbo);
            if (width != _playbackWidth || height != _playbackHeight)
            {
                _playbackFrameAvailable = false;
                GL.BindTexture(TextureTarget.Texture2D, _playbackTexture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, width, height,
                    0, PixelFormat.Rgb, PixelType.UnsignedByte, 0);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, _playbackTexture, 0);
                if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                    throw new InvalidOperationException(L.T("PreviewBufferError"));
                _playbackWidth = width;
                _playbackHeight = height;
                float zoom = renderer.Camera.Zoom;
                renderer.Resize(width, height);
                // The edit and preview toolbars can leave different viewport heights.
                if (_restoringEditViewport)
                {
                    renderer.Camera.Zoom = zoom;
                    renderer.Camera.MarkDirty();
                }
            }
            // The syncing status can wrap the toolbar until the editor revision is ready.
            if (_restoringEditViewport && HasCurrentPlayback)
                _restoringEditViewport = false;

            GL.Viewport(0, 0, width, height);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.ScissorTest);
            bool editing = _playbackView == PlaybackView.Edit;
            bool tilesOnly = scene is null && editing && _tilesOnly;
            renderer.SetEditorTilesOnly(tilesOnly);
            var background = tilesOnly ? new Vector4(.059f, .065f, .083f, 1) : renderer.BackgroundColor;
            background.W = 1;
            renderer.ShowDecorations = !editing || (!_hideAllDecorations && !tilesOnly);
            renderer.ShowPositionTrackGuides = editing && !_canvasOnly;
            renderer.CollectDecorationQuads = editing && !tilesOnly && !_canvasOnly;
            if (!tilesOnly && scene is null && renderer.ShouldStageEditorPreview)
            {
                // Keep presenting the last complete frame while the newest seek target uploads.
                _playbackFramePending = !renderer.TryRenderEditorPreview(background);
                if (!_playbackFramePending)
                    _playbackFrameAvailable = true;
            }
            else
            {
                GL.ClearColor(background.X, background.Y, background.Z, background.W);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                if (scene is { } view) renderer.RenderEditorScene(background, view);
                else if (tilesOnly) renderer.RenderEditorTiles();
                else renderer.RenderAfterClear(background);
                _playbackFrameAvailable = true;
            }
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDraw);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousRead);
            GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        }
    }

    private void FeedEditorKey(KeyboardKeyEventArgs args, bool pressed)
    {
        if (_audioCalibration.IsOpen)
        {
            if (pressed && args.Key == Keys.Space && _audioCalibration.IsMeasuring)
            {
                if (!args.IsRepeat && _playback is { } renderer)
                    _audioCalibration.Tap(renderer);
            }
            else
                FeedKey(args, pressed);
            return;
        }
        if (pressed && !_settingsWindow.IsOpen && ApplicationWindowModeController.IsToggleShortcut(args))
        {
            if (!args.IsRepeat)
                _windowMode.RequestToggle();
            return;
        }
        if (pressed && args.Key == Keys.H &&
            (args.Modifiers & (KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Super)) == KeyModifiers.Alt)
        {
            if (!args.IsRepeat)
            {
                if (args.Shift) _shortcutsToggleRequested = true;
                else _canvasOnlyToggleRequested = true;
            }
            return;
        }
        if (pressed && args.Key == Keys.Escape)
        {
            if (args.IsRepeat)
                return;
            if (_playbackView != PlaybackView.Edit && !_settingsWindow.IsOpen)
            {
                ReturnToEdit(selectPlaybackFloor: args.Shift);
                // Do not let this same Escape clear the restored selection in the edit view.
                return;
            }
        }
        // Manual-play chords are handled by the gameplay input path. Submitting the same
        // key to ImGui would toggle it again after switching back to auto playback.
        if (_playbackView != PlaybackView.Manual || !args.Alt || args.Key is not (Keys.A or Keys.N))
            FeedKey(args, pressed);
        FeedPlaybackKey(args, pressed);
    }

    private void OpenAudioCalibration()
    {
        if (_playbackView is PlaybackView.Auto or PlaybackView.Manual)
        {
            ReleasePlaybackInput();
            PauseToPreview();
        }
        _audioCalibration.Open();
    }

    private void ReturnToEdit(bool selectPlaybackFloor = false)
    {
        if (_playbackView == PlaybackView.Edit)
            return;

        int? floor = _displayedRevision?.Document == _document && _playback is { Level: not null } renderer
            ? Math.Clamp(renderer.CurrentIndex, 0, _document.LastFloor) : null;
        SwitchPlaybackView(PlaybackView.Edit);
        if (floor is int index)
        {
            if (selectPlaybackFloor)
                SelectFloor(index, true);
            else
                _centerEditFloor = index;
        }
    }

    private void FeedPlaybackKey(KeyboardKeyEventArgs args, bool pressed)
    {
        if (pressed && _settingsWindow.IsOpen)
            return;
        if (_playbackView != PlaybackView.Manual || !HasCurrentPlayback || _playback is not { } renderer ||
            args.IsRepeat || args.Key is Keys.Unknown or Keys.LeftAlt or Keys.RightAlt or
                Keys.LeftControl or Keys.RightControl or Keys.LeftSuper or Keys.RightSuper ||
            pressed && ImGui.GetIO()->WantTextInput != 0)
            return;
        if (args.Alt && args.Key is Keys.A or Keys.N)
        {
            if (pressed)
            {
                if (args.Key == Keys.A) SetManualPlayMode(!_manualPlay);
                else SetNoFail(!_noFail);
            }
            return;
        }
        if (pressed && (renderer.PlaySession.Failed ||
                       !renderer.IsPlaying && args.Key is Keys.Enter or Keys.KeyPadEnter))
        {
            renderer.Play();
            return;
        }
        if (args.Key == Keys.Space)
        {
            if (pressed) renderer.TogglePlay();
            return;
        }
        renderer.PlayInput.Push(args.Key, pressed, _playbackClock.Elapsed.TotalSeconds);
    }

    private void FeedPlaybackMouse(MouseButton button, bool pressed)
    {
        if (_playbackView != PlaybackView.Manual || !HasCurrentPlayback || _playback is not { } renderer ||
            pressed && (!_playbackHovered || _settingsWindow.IsOpen ||
                        _showSceneView && button is MouseButton.Left or MouseButton.Middle))
            return;
        if (pressed && renderer.PlaySession.Failed)
            renderer.Play();
        else
            renderer.PlayInput.Push(button, pressed, _playbackClock.Elapsed.TotalSeconds);
    }

    private void ReleasePlaybackInput() => _playback?.PlayInput.ReleaseAll(_playbackClock.Elapsed.TotalSeconds);

    private void DisposePlayback()
    {
        _audioMonitor.ClearSource();
        CancelTimelineWaveform();
        _playback?.Dispose();
        if (_playbackFbo != 0)
            GL.DeleteFramebuffer(_playbackFbo);
        if (_playbackTexture != 0)
            GL.DeleteTexture(_playbackTexture);
    }
}
