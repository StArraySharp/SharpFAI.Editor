using System.Diagnostics;
using ImGui4NET;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private enum PlaybackView { Edit, Preview, Auto, Manual }
    private readonly record struct PlaybackRevision(EditorDocument Document, int Version, string SourcePath, double Speed);
    private readonly record struct EditSelection(int Floor, bool HasFloor, int? RangeAnchor, int EventPage,
        bool InspectDecoration, JObject? Decoration, JObject[] Decorations);

    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
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
    private bool _tilesOnly;
    private int _playbackSpeed = 50;
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
    private int _playbackFbo;
    private int _playbackTexture;
    private int _playbackWidth;
    private int _playbackHeight;
    private int _smokePlaybackStage;
    private int _smokeFloor;
    private int _smokeDecorationIndex = -1;
    private double _smokeDeadline;
    private ImVec2 _smokeViewportCenter;
    private float _smokeViewportZoom;
    private (Vector2 Position, float Zoom) _smokeEditCamera;
    private double _playbackTickTime;
    private double _smokeRateWarmupUntil;
    private (double WallTime, double ChartTime)? _smokeRateSample;
    private object? _smokeLoadTimings;
    private JObject? _smokeAddedEvent;
    private JObject? _smokeSelectedDecoration;
    private double _smokeOffset;
    private SharpFAI.Serialization.Level? _smokeLiveChart;

    private PlaybackRevision CurrentPlaybackRevision => new(_document, _document.ChangeVersion,
        _document.PlaybackPath, _appliedPlaybackSpeed);
    private bool HasCurrentPlayback => _playback is { IsLoading: false, LoadError: null, Level: not null }
                                      && _displayedRevision == CurrentPlaybackRevision;

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
        _playbackView = view;
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
            _centerEditSelection = false;
            _playback?.Pause();
            _playback?.SetManualPlay(false);
            if (_savedEditCamera is { } camera && _playback is { Level: not null } renderer &&
                _displayedRevision?.Document == _document)
            {
                RestoreEditCamera(renderer, camera);
                _restoringEditViewport |= previous != PlaybackView.Edit;
            }
            return;
        }

        _restoringEditViewport = false;
        if (previous == PlaybackView.Edit)
        {
            _savedEditSelection = new(_floor, _hasFloorSelection, _rangeAnchor, _eventPage, _inspectDecoration,
                _decoration, _selectedDecorations.ToArray());
            if (_playback is { Level: not null } editRenderer)
                _savedEditCamera = (editRenderer.Camera.Position, editRenderer.Camera.Zoom);
        }
        if (_startPlayback && rate == PlaybackRate.Configured && (!_hasFloorSelection || _inspectDecoration))
            _playbackFloor = 0;
        else if (_startPlayback && startFloor is int floor)
            _playbackFloor = floor;
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
        _startPlayback = false;
        _playback?.Pause();
        _playback?.SetManualPlay(false);
        _playbackView = PlaybackView.Preview;
    }

    private void UpdatePlayback()
    {
        double now = _playbackClock.Elapsed.TotalSeconds;
        bool editing = _playbackView == PlaybackView.Edit;
        if (_playback is null)
        {
            _playback = new LevelRenderer();
            ApplyPreviewAudioSettings(_playback);
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
        if (_smokeTest) _playbackTickTime = now;
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
            else if (!FinishPlaybackRevision(renderer, pending, target, _pendingEffects, out effectsFailed))
                return;
            _pendingRevision = null;
        }

        if (_displayedRevision != target)
        {
            if (_failedRevision == target) return;
            if (!renderer.IsLoading && renderer.Level is not null && _displayedRevision is { } current &&
                SamePlaybackSource(current, target))
            {
                try
                {
                    bool effects = !effectsFailed && current.Speed == target.Speed &&
                        renderer.BeginEditorEffectUpdate(_document.EffectChangesSince(current.Version));
                    if (effects && !FinishPlaybackRevision(renderer, target, target, effects: true, out effectsFailed))
                        return;
                    if (_displayedRevision == target) goto PlaybackReady;

                    double delay = editing && _pendingTiles.Count > 0 ? 0.035 : 0.12;
                    if (!effectsFailed && !_startPlayback && !_document.CanReusePlaybackChart(current.Version) &&
                        now - _lastPreviewEdit < delay) return;

                    var settings = (JObject)_document.Settings.DeepClone();
                    if (target.Speed != 1) settings["pitch"] = Number(settings["pitch"], 100) * target.Speed;
                    var snapshot = _document.CanReusePlaybackChart(current.Version) ? null : _document.SnapshotForPlayback();
                    if (!renderer.BeginEditorRevision(snapshot, settings))
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
            renderer.PlaySession.NoFail = _shortcutNoFail;
            renderer.SetManualPlay(manual);
            if (!renderer.IsPlaying)
                renderer.Play();
        }
        else if (_playbackView == PlaybackView.Auto && !renderer.IsPlaying)
            _playbackView = PlaybackView.Preview;
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
        ImDrawList.AddRectFilled(draw, start, end, WidgetDraw.U32(22, 30, 43, 242), 4);
        ImDrawList.AddText(draw, WidgetDraw.V2(start.x + 7, start.y + 4), WidgetDraw.U32(141, 237, 222), text);
    }

    private static PlaybackRate HeldPlaybackRate => ImGui.GetIO()->KeyCtrl != 0 ? PlaybackRate.Configured : PlaybackRate.Normal;

    private void DrawPlaybackSpeed()
    {
        int speed = _playbackSpeed;
        ImGui.SetNextItemWidth(76 * _uiScale);
        if (ImGui.DragInt("##playback-speed", ref speed, 1, 1, 1000, "%d%%",
                (int)ImGuiSliderFlags.ImGuiSliderFlags_AlwaysClamp) != 0)
        {
            _playbackSpeed = Math.Clamp(speed, 1, 1000);
            _wheelSpeedRemainder = 0;
        }
        if (ImGui.IsItemHovered() != 0)
            ImGui.SetTooltip(L.T("PlaybackSpeedHelp"));
        ContinueToolbar(ButtonWidth("100%"));
        if (ImGui.Button("100%##reset-speed", default) != 0)
        {
            _playbackSpeed = 100;
            _wheelSpeedRemainder = 0;
        }
        WidgetDraw.ItemTooltip(L.T("ResetPlaybackSpeed"));
    }

    private static bool SamePlaybackSource(PlaybackRevision first, PlaybackRevision second) =>
        first.Document == second.Document && first.SourcePath == second.SourcePath;

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
        if (ImGui.Button(L.T("ReturnToEdit"), default) != 0)
        {
            SwitchPlaybackView(PlaybackView.Edit);
            return;
        }

        ImGui.SameLine();
        bool ready = HasCurrentPlayback;
        LevelRenderer? renderer = _playback;
        if (!ready || renderer is null)
        {
            string status = renderer?.LoadError ?? (renderer?.IsLoading == true ? L.F("SyncStage", L.LoadPhase(renderer.LoadPhase)) : L.T("Syncing"));
            ImGui.TextUnformatted(status);
        }
        else
        {
            ImGui.TextUnformatted($"{renderer.CurrentIndex:N0} / {renderer.FloorCount - 1:N0}   {renderer.PlayTime:F3} s");
            if (_playbackView == PlaybackView.Manual)
            {
                ImGui.SameLine();
                bool noFail = renderer.PlaySession.NoFail;
                if (Toggle(L.T("NoFail"), ref noFail))
                    renderer.PlaySession.NoFail = _shortcutNoFail = noFail;
            }
            else
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

        ImVec2 size = ImGui.GetContentRegionAvail();
        size.x = Math.Max(1, size.x);
        size.y = Math.Max(1, size.y);
        if (ready && renderer is not null)
            RenderPlaybackFrame(renderer, size);

        DrawPlaybackImage(size);
        _playbackHovered = ImGui.IsItemHovered() != 0;

        if (ready && renderer is not null && _playbackView == PlaybackView.Preview)
            NavigatePlaybackPreview(renderer);
        if (ready && renderer is { CanNavigateEditorPlayback: true } && _playbackView is PlaybackView.Auto or PlaybackView.Manual)
            NavigateActivePlayback(renderer);
        else
            _playbackPan = false;

        if (ready && renderer is not null && _playbackView == PlaybackView.Manual)
        {
            ImVec2 min = ImGui.GetItemRectMin();
            ImVec2 max = ImGui.GetItemRectMax();
            ImDrawList* draw = ImGui.GetWindowDrawList();
            ImDrawList.PushClipRect(draw, min, max, 1);
            ManualPlayHud.Draw(renderer, draw, min, max, L.T);
            ImDrawList.PopClipRect(draw);
        }
    }

    private void DrawPlaybackImage(ImVec2 size)
    {
        if (_playbackFrameAvailable)
            ImGui.Image(new ImTextureRef { _TexID = (uint)_playbackTexture }, size, WidgetDraw.V2(0, 1), WidgetDraw.V2(1, 0));
        else
            ImGui.Dummy(size);
        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        DrawSpeedIndicator(min, max);
        if (_smokeTest)
            _smokeViewportCenter = WidgetDraw.V2((min.x + max.x) * .5f, (min.y + max.y) * .5f);
    }

    private void NavigatePlaybackPreview(LevelRenderer renderer)
    {
        ImGuiIO* io = ImGui.GetIO();
        ImVec2 origin = ImGui.GetItemRectMin();
        ImVec2 scale = io->DisplayFramebufferScale;
        Vector2 cursor = new((io->MousePos.x - origin.x) * scale.x, (io->MousePos.y - origin.y) * scale.y);
        Vector2 delta = new(io->MouseDelta.x * scale.x, io->MouseDelta.y * scale.y);
        var mouse = UpdateCanvasGesture(_playbackHovered);
        if (mouse == CanvasGesture.Drag)
            renderer.Pan(delta);
        else if (mouse == CanvasGesture.Click)
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

    private void RenderPlaybackFrame(LevelRenderer renderer, ImVec2 size)
    {
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
            bool tilesOnly = editing && _tilesOnly;
            var background = tilesOnly ? new Vector4(.059f, .065f, .083f, 1) : renderer.BackgroundColor;
            GL.ClearColor(background.X, background.Y, background.Z, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            renderer.ShowDecorations = !editing || (!_hideAllDecorations && !tilesOnly);
            renderer.CollectDecorationQuads = editing && !tilesOnly;
            if (tilesOnly) renderer.RenderEditorTiles();
            else renderer.Render();
            _playbackFrameAvailable = true;
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
        if (pressed && args.Key == Keys.Escape)
        {
            if (args.IsRepeat)
                return;
            if (_playbackView != PlaybackView.Edit && !_settingsWindow.IsOpen)
            {
                int? floor = args.Shift && HasCurrentPlayback ? _playback!.CurrentIndex : null;
                SwitchPlaybackView(PlaybackView.Edit);
                if (floor is int index)
                    SelectFloor(index, false);
                // Do not let this same Escape clear the restored selection in the edit view.
                return;
            }
        }
        FeedKey(args, pressed);
        FeedPlaybackKey(args, pressed);
    }

    private void FeedPlaybackKey(KeyboardKeyEventArgs args, bool pressed)
    {
        if (pressed && _settingsWindow.IsOpen)
            return;
        if (_playbackView != PlaybackView.Manual || !HasCurrentPlayback || _playback is not { } renderer ||
            args.IsRepeat || args.Key is Keys.Unknown or Keys.LeftAlt or Keys.RightAlt ||
            pressed && ImGui.GetIO()->WantTextInput != 0)
            return;
        if (args.Alt && args.Key is Keys.A or Keys.N)
        {
            if (pressed)
            {
                if (args.Key == Keys.A) ToggleShortcutAutoplay();
                else ToggleShortcutNoFail();
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
            pressed && (!_playbackHovered || _settingsWindow.IsOpen))
            return;
        if (pressed && renderer.PlaySession.Failed)
            renderer.Play();
        else
            renderer.PlayInput.Push(button, pressed, _playbackClock.Elapsed.TotalSeconds);
    }

    private void ReleasePlaybackInput() => _playback?.PlayInput.ReleaseAll(_playbackClock.Elapsed.TotalSeconds);

    private void DisposePlayback()
    {
        _playback?.Dispose();
        if (_playbackFbo != 0)
            GL.DeleteFramebuffer(_playbackFbo);
        if (_playbackTexture != 0)
            GL.DeleteTexture(_playbackTexture);
    }

    private void StartPlaybackSmokeTest()
    {
        _smokeFloor = Math.Min(2, _document.LastFloor - 1);
        SelectFloor(Math.Max(0, _smokeFloor), true);
        _smokePlaybackStage = -4;
        _smokeDeadline = _playbackClock.Elapsed.TotalSeconds + 120;
    }

    private void FeedPlaybackSmokeInput(ImGuiIO* io)
    {
        switch (_smokePlaybackStage)
        {
            case 9:
                ImGuiIO.AddMousePosEvent(io, _smokeViewportCenter.x, _smokeViewportCenter.y);
                ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Ctrl, 1);
                break;
            case 10:
                FeedEditorWheel(io, 0, 1, ctrl: true, shift: false);
                break;
            case 11:
                FeedEditorWheel(io, 0, 1, ctrl: true, shift: true);
                ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Ctrl, 0);
                break;
            case 12:
                FeedEditorWheel(io, 0, 1, ctrl: false, shift: false);
                PressPlaybackSmokeKey(Keys.Escape);
                break;
            case 13 when _playbackView == PlaybackView.Edit:
                FeedEditorWheel(io, 0, -2, ctrl: true, shift: false);
                PressPlaybackSmokeKey(Keys.Space, KeyModifiers.Control);
                break;
            case 14:
                ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiMod_Ctrl, 0);
                ImGuiIO.AddKeyEvent(io, ImGuiKey.ImGuiKey_Space, 0);
                break;
            case 16 when _playbackView != PlaybackView.Auto:
                FeedKey(new KeyboardKeyEventArgs(Keys.Space, 0, 0, false), true);
                break;
            case 17:
                FeedKey(new KeyboardKeyEventArgs(Keys.Space, 0, 0, false), false);
                ImGuiIO.AddMousePosEvent(io, _smokeViewportCenter.x, _smokeViewportCenter.y);
                break;
            case 18:
                ImGuiIO.AddMousePosEvent(io, _smokeViewportCenter.x, _smokeViewportCenter.y);
                FeedEditorWheel(io, 0, -1, ctrl: false, shift: false);
                break;
            case 19:
                ImGuiIO.AddMousePosEvent(io, _smokeViewportCenter.x, _smokeViewportCenter.y);
                break;
            case 21:
                ImGuiWindow* canvas = ImGui.FindWindowByName(CanvasWindow);
                ImGuiIO.AddMousePosEvent(io, canvas->WorkRect.Min.x + 8,
                    canvas->WorkRect.Min.y + ImGui.GetFrameHeight() * .5f);
                ImGuiIO.AddMouseButtonEvent(io, 0, 1);
                break;
            case 22 when _playbackView == PlaybackView.Edit:
                FeedKey(new KeyboardKeyEventArgs(Keys.Space, 0, KeyModifiers.Control, false), true);
                FeedKey(new KeyboardKeyEventArgs(Keys.Space, 0, 0, false), false);
                break;
            case 23: case 25: case 28:
                ImGuiIO.AddMouseButtonEvent(io, 0, 0);
                break;
            case 24:
                ImGuiIO.AddMousePosEvent(io, _angleSmokeInput.x, _angleSmokeInput.y);
                ImGuiIO.AddMouseButtonEvent(io, 0, 1);
                break;
            case 27:
                ImGuiIO.AddMousePosEvent(io, _smokeViewportCenter.x, _smokeViewportCenter.y);
                ImGuiIO.AddMouseButtonEvent(io, 0, 1);
                break;
            case 26: case 29 when _playbackView == PlaybackView.Edit:
                FeedWebKey("Space", down: true, ctrl: true, shift: false);
                FeedWebKey("Space", down: false, ctrl: false, shift: false);
                break;
            case 35 when _playbackView == PlaybackView.Edit && _playbackFloor is null:
            case 44 when _playbackView == PlaybackView.Edit:
            case 42:
                PressPlaybackSmokeKey(Keys.Space, KeyModifiers.Control);
                break;
            case 36:
                PressPlaybackSmokeKey(Keys.Space);
                break;
            case 37 or 39 when _floor != 0:
                PressPlaybackSmokeKey(Keys.Home);
                break;
            case 20 or 38 or 45 when _playbackView != PlaybackView.Edit:
            case 43:
                PressPlaybackSmokeKey(Keys.Escape);
                break;
            case 40 when _playbackView != PlaybackView.Edit:
                FeedWebKey("Escape", down: true, ctrl: false, shift: true);
                FeedWebKey("Escape", down: false, ctrl: false, shift: false);
                break;
        }
    }

    private void PressPlaybackSmokeKey(Keys key, KeyModifiers modifiers = 0)
    {
        FeedEditorKey(new KeyboardKeyEventArgs(key, 0, modifiers, false), true);
        FeedEditorKey(new KeyboardKeyEventArgs(key, 0, 0, false), false);
    }

    private void VerifyPlaybackSmokeTest()
    {
        if (_playback?.LoadError is { } error)
            throw new InvalidOperationException(error);
        if (_playbackClock.Elapsed.TotalSeconds > _smokeDeadline)
            throw new TimeoutException("Player preview sync timed out.");
        if (!HasCurrentPlayback || !_playbackFrameAvailable || _playback is not { } renderer)
            return;

        _smokeLoadTimings ??= renderer.LoadTimings;
        if (!ReferenceEquals(_smokeLoadTimings, renderer.LoadTimings))
            throw new InvalidOperationException("An in-place edit or playback rate change reloaded the whole chart.");

        switch (_smokePlaybackStage)
        {
            case -4:
                _tilesOnly = true;
                _smokePlaybackStage++;
                break;
            case -3:
                Span<Vector2> hiddenSprite = stackalloc Vector2[4];
                if (renderer.DrawCalls == 0 || renderer.ShowDecorations ||
                    (_smokeDecorationIndex >= 0 && renderer.TryGetDecorationQuad(_smokeDecorationIndex, hiddenSprite)))
                    throw new InvalidOperationException("Tiles-only mode did not render tiles and hide decorations.");
                _tilesOnly = false;
                _smokePlaybackStage++;
                break;
            case -2:
                Span<Vector2> sprite = stackalloc Vector2[4];
                if (_smokeDecorationIndex >= 0 && (!renderer.TryGetDecorationQuad(_smokeDecorationIndex, sprite) ||
                    !InsideQuad(sprite, (sprite[0] + sprite[2]) * 0.5f)))
                    throw new InvalidOperationException("A visible image decoration did not resolve to its rendered quad.");
                renderer.Zoom(1, new(renderer.Camera.ViewportWidth * .5f, renderer.Camera.ViewportHeight * .5f));
                renderer.Pan(new(12, -9));
                _smokeEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
                int oldCount = renderer.FloorCount;
                InsertTile(0);
                InsertTile(90);
                if (PendingTileCenters(renderer).Count != 3 || renderer.FloorCount != oldCount)
                    throw new InvalidOperationException("Rapid tile insertion did not produce immediate outlines.");
                _smokeFloor = _floor;
                _smokePlaybackStage++;
                break;
            case -1:
                if (_playbackView != PlaybackView.Edit || renderer.DrawCalls == 0 ||
                    renderer.SelectedFloor != _smokeFloor || renderer.FloorCount != _document.LastFloor + 1 ||
                    _pendingTiles.Count != 0)
                    throw new InvalidOperationException("The edit viewport did not render the Player scene.");
                if ((renderer.Camera.Position, renderer.Camera.Zoom) != _smokeEditCamera)
                    throw new InvalidOperationException("Tile insertion changed the edit camera position or zoom.");
                SwitchPlaybackView(PlaybackView.Preview);
                _smokePlaybackStage++;
                break;
            case 0:
                if (renderer.IsPlaying || renderer.SelectedFloor != Math.Max(0, _smokeFloor))
                    throw new InvalidOperationException("The preview did not seek to the selected tile.");
                _document.Set(_document.Settings, "bpm", 123.456);
                _smokePlaybackStage++;
                break;
            case 1:
                if (renderer.IsPlaying || renderer.Level!.GetSetting<double>("bpm") != 123.456)
                    throw new InvalidOperationException("The paused preview did not sync to the edited chart.");
                SwitchPlaybackView(PlaybackView.Auto);
                _smokePlaybackStage++;
                break;
            case 2:
                if (!renderer.IsPlaying || renderer.ManualPlayEnabled || renderer.Level!.GetSetting<double>("bpm") != 123.456)
                    throw new InvalidOperationException("Autoplay did not use the latest chart.");
                if (renderer.Level!.root["settings"]?["zoom"]?.Value<float>() is > 0 && !renderer.HasCameraEventZoom &&
                    MathF.Abs(renderer.Camera.Zoom - renderer.GetExportZoom()) > 1e-5f)
                    throw new InvalidOperationException("Autoplay inherited the edit zoom instead of the chart zoom.");
                SwitchPlaybackView(PlaybackView.Manual);
                _smokePlaybackStage++;
                break;
            case 3:
                if (!renderer.IsPlaying || !renderer.ManualPlayEnabled)
                    throw new InvalidOperationException("Manual play did not start.");
                SwitchPlaybackView(PlaybackView.Edit);
                if (renderer.IsPlaying || renderer.ManualPlayEnabled)
                    throw new InvalidOperationException("Manual play did not stop on return to the editor.");
                if ((renderer.Camera.Position, renderer.Camera.Zoom) != _smokeEditCamera)
                    throw new InvalidOperationException("Returning from playback did not restore the edit camera.");
                _document.Set(_document.Settings, "pitch", 125);
                _playbackSpeed = 75;
                SwitchPlaybackView(PlaybackView.Auto, PlaybackRate.Configured);
                _smokePlaybackStage++;
                break;
            case 4:
                VerifySmokePitch(renderer, 93.75);
                _playbackSpeed = 100;
                SwitchPlaybackView(PlaybackView.Auto, PlaybackRate.Configured);
                _smokePlaybackStage++;
                break;
            case 5:
                VerifySmokePitch(renderer, 125);
                _playbackSpeed = 150;
                SwitchPlaybackView(PlaybackView.Manual, PlaybackRate.Configured);
                _smokePlaybackStage++;
                break;
            case 6:
                VerifySmokePitch(renderer, 187.5);
                SwitchPlaybackView(PlaybackView.Auto, PlaybackRate.Half);
                _smokePlaybackStage++;
                break;
            case 7:
                VerifySmokePitch(renderer, 93.75);
                SwitchPlaybackView(PlaybackView.Edit);
                _smokePlaybackStage++;
                break;
            case 8:
                _playbackSpeed = 64;
                _smokeViewportZoom = renderer.Camera.Zoom;
                _smokePlaybackStage++;
                break;
            case 9:
                if (!ShowSpeedIndicator)
                    throw new InvalidOperationException("Holding Ctrl did not show the playback speed indicator.");
                _smokePlaybackStage++;
                break;
            case 10:
                if (_playbackSpeed != 74 || renderer.Camera.Zoom != _smokeViewportZoom)
                    throw new InvalidOperationException("Ctrl+wheel did not change playback speed by 10 points without zooming.");
                _smokePlaybackStage++;
                break;
            case 11:
                if (_playbackSpeed != 75 || renderer.Camera.Zoom != _smokeViewportZoom)
                    throw new InvalidOperationException("Ctrl+Shift+wheel did not apply the fine step after modifier release.");
                _smokePlaybackStage++;
                break;
            case 12:
                if (_playbackSpeed != 75 || renderer.Camera.Zoom == _smokeViewportZoom)
                    throw new InvalidOperationException("Unmodified wheel did not zoom without changing playback speed.");
                if (_hasFloorSelection || _floor != _smokeFloor || _floor == 0)
                    throw new InvalidOperationException("Could not clear the selection while retaining a nonzero edit cursor.");
                _smokePlaybackStage++;
                break;
            case 13:
                if (_playbackView != PlaybackView.Auto || _playbackSpeed != 55)
                    throw new InvalidOperationException("Ctrl+Space did not start playback with the same-frame wheel adjustment.");
                if (renderer.SelectedFloor != 0)
                    throw new InvalidOperationException("Ctrl+Space with no selected tile did not start at tile zero.");
                VerifySmokePitch(renderer, 68.75);
                _smokeRateWarmupUntil = _playbackTickTime + .35;
                _smokePlaybackStage++;
                break;
            case 14:
                if (_playbackTickTime < _smokeRateWarmupUntil) return;
                _smokeRateSample ??= (_playbackTickTime, renderer.PlayTime);
                var (wallTime, chartTime) = _smokeRateSample.Value;
                double elapsed = _playbackTickTime - wallTime;
                if (elapsed < 1.25) return;
                double measured = (renderer.PlayTime - chartTime) / elapsed;
                if (Math.Abs(measured - .6875) > .10)
                    throw new InvalidOperationException($"Expected playback at 68.75% of chart time, measured {measured:P1}.");
                Console.WriteLine($"Measured Ctrl+Space chart-time advance: {measured:P1} (expected 68.75%).");
                SwitchPlaybackView(PlaybackView.Edit);
                _smokePlaybackStage++;
                break;
            case 15:
                if (_hasFloorSelection || _floor != _smokeFloor)
                    throw new InvalidOperationException("Returning to edit did not restore the empty selection.");
                SelectFloor(_smokeFloor, false);
                _smokePlaybackStage++;
                break;
            case 16:
                if (_playbackView == PlaybackView.Auto && _startPlayback) return;
                VerifySmokePitch(renderer, 125);
                _smokePlaybackStage++;
                break;
            case 17:
                _smokeViewportZoom = renderer.Camera.Zoom;
                _smokePlaybackStage++;
                break;
            case 18:
                _smokePlaybackStage++;
                break;
            case 19:
                if (renderer.CanNavigateEditorPlayback && _smokeViewportZoom > renderer.Camera.MinZoom &&
                    renderer.Camera.Zoom >= _smokeViewportZoom)
                    throw new InvalidOperationException("The plain wheel did not zoom during autoplay.");
                Console.WriteLine(renderer.CanNavigateEditorPlayback ? "Playback viewport navigation enabled." :
                    "Playback viewport navigation locked by authored camera motion.");
                _smokePlaybackStage++;
                break;
            case 20:
                if (_playbackView != PlaybackView.Edit || !_hasFloorSelection || _floor != _smokeFloor ||
                    _rangeAnchor is not null || renderer.SelectedFloor != _smokeFloor)
                    throw new InvalidOperationException("Escape did not restore the single edit tile selection.");
                _smokePlaybackStage++;
                break;
            case 21:
                if (ImGui.IsAnyItemActive() == 0)
                    throw new InvalidOperationException("The canvas toolbar button was not held by the mouse.");
                _smokePlaybackStage++;
                break;
            case 22:
                if (_playbackView != PlaybackView.Auto)
                    throw new InvalidOperationException("A mouse-active toolbar control swallowed Ctrl+Space.");
                VerifySmokePitch(renderer, 68.75);
                SwitchPlaybackView(PlaybackView.Edit);
                _smokePlaybackStage++;
                break;
            case 23:
                _showArbitraryAngle = true;
                _smokePlaybackStage++;
                break;
            case 24: case 25: case 27: case 28:
                _smokePlaybackStage++;
                break;
            case 26:
                if (ImGui.GetIO()->WantTextInput == 0 || _playbackView != PlaybackView.Edit)
                    throw new InvalidOperationException("Ctrl+Space was not reserved by the focused angle text input.");
                _smokePlaybackStage++;
                break;
            case 29:
                if (_playbackView != PlaybackView.Auto)
                    throw new InvalidOperationException("Web Ctrl+Space did not start configured playback after text input lost focus.");
                VerifySmokePitch(renderer, 68.75);
                _smokeLiveChart = renderer.Level;
                _document.Set(_document.Settings, "volume", Number(_document.Settings["volume"], 100) == 42 ? 43 : 42);
                _smokePlaybackStage++;
                break;
            case 30:
                if (!renderer.IsPlaying || !ReferenceEquals(renderer.Level, _smokeLiveChart) ||
                    renderer.LastEditorRevisionLayers != LevelRenderer.EditorLayers.Mixer ||
                    !JToken.DeepEquals(renderer.Level!.root["settings"]?["volume"], _document.Settings["volume"]))
                    throw new InvalidOperationException("Changing volume did not preserve live playback.");
                _document.Undo();
                SwitchPlaybackView(PlaybackView.Edit);
                _smokePlaybackStage++;
                break;
            case 31:
                _smokeEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
                _smokeOffset = Number(_document.Settings["offset"], 0);
                _document.Set(_document.Settings, "offset", _smokeOffset + 125);
                _smokePlaybackStage++;
                break;
            case 32:
                if (!renderer.LastEditorRevisionLayers.HasFlag(LevelRenderer.EditorLayers.Timing) ||
                    renderer.LastEditorRevisionLayers.HasFlag(LevelRenderer.EditorLayers.Track) ||
                    renderer.Level!.GetSetting<double>("offset") != _smokeOffset + 125 ||
                    (renderer.Camera.Position, renderer.Camera.Zoom) != _smokeEditCamera)
                    throw new InvalidOperationException("Offset edit did not update timing without rebuilding the track.");
                _document.Undo();
                _smokeAddedEvent = JObject.Parse("""{"floor":1,"eventType":"MoveCamera","duration":1,"position":[1,2]}""");
                if (!_document.Add(_smokeAddedEvent, decoration: false))
                    throw new InvalidOperationException("Could not insert the smoke test event.");
                _smokePlaybackStage++;
                break;
            case 33:
                if (!renderer.Level!.actions.Any(item => JToken.DeepEquals(item, _smokeAddedEvent)) ||
                    (renderer.Camera.Position, renderer.Camera.Zoom) != _smokeEditCamera)
                    throw new InvalidOperationException("An event insertion was not applied without moving the camera.");
                _document.Undo();
                _smokePlaybackStage++;
                break;
            case 34:
                if (renderer.Level!.actions.Any(item => JToken.DeepEquals(item, _smokeAddedEvent)))
                    throw new InvalidOperationException("An undone event insertion remained in the preview.");
                SelectFloor(_smokeFloor - 1, false);
                MoveFloorSelection(_smokeFloor, extend: true);
                _centerEditSelection = false;
                _smokePlaybackStage++;
                break;
            case 35:
                if (_playbackView == PlaybackView.Edit)
                {
                    _smokeEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
                    return;
                }
                VerifySmokePitch(renderer, 68.75);
                _smokePlaybackStage++;
                break;
            case 36:
                if (_playbackView != PlaybackView.Preview || renderer.IsPlaying)
                    throw new InvalidOperationException("Space did not pause playback into preview.");
                _smokePlaybackStage++;
                break;
            case 37: case 39:
                if (_playbackView != PlaybackView.Preview || _floor != 0 || _rangeAnchor is not null ||
                    renderer.SelectedFloor != 0)
                    throw new InvalidOperationException("Home did not navigate to tile zero in preview.");
                _smokePlaybackStage++;
                break;
            case 38:
                if (_playbackView != PlaybackView.Edit || !_hasFloorSelection || _floor != _smokeFloor ||
                    _rangeAnchor != _smokeFloor - 1 || renderer.SelectedFloor != _smokeFloor)
                    throw new InvalidOperationException($"Escape did not restore the pre-playback tile range: {_playbackView}, selected={_hasFloorSelection}, anchor={_rangeAnchor}, floor={_floor}, renderer={renderer.SelectedFloor}, expected floor={_smokeFloor}.");
                if ((renderer.Camera.Position, renderer.Camera.Zoom) != _smokeEditCamera)
                    throw new InvalidOperationException($"Escape did not restore the edit camera: got {(renderer.Camera.Position, renderer.Camera.Zoom)}, expected {_smokeEditCamera}.");
                SwitchPlaybackView(PlaybackView.Preview);
                _smokePlaybackStage++;
                break;
            case 40:
                if (_playbackView != PlaybackView.Edit || !_hasFloorSelection || _floor != 0 || _rangeAnchor is not null)
                    throw new InvalidOperationException("Web Shift+Escape did not select the current preview tile.");
                OpenFindFloor();
                _smokePlaybackStage++;
                break;
            case 41: case 42:
                if (_playbackView != PlaybackView.Edit || !ShortcutContextBlocked)
                    throw new InvalidOperationException("Ctrl+Space was not reserved by the open Find Floor dialog.");
                _smokePlaybackStage++;
                break;
            case 43:
                if (_playbackView != PlaybackView.Edit || ShortcutContextBlocked || !_hasFloorSelection)
                    throw new InvalidOperationException("Escape did not close the dialog without changing the edit selection.");
                _smokeSelectedDecoration = _document.Decorations.OfType<JObject>().FirstOrDefault();
                if (_smokeSelectedDecoration is null)
                    goto case 46;
                SelectFloor(_smokeFloor, false);
                SelectDecoration(_smokeSelectedDecoration);
                _smokePlaybackStage++;
                break;
            case 44:
                VerifySmokePitch(renderer, 68.75);
                if (renderer.SelectedFloor != 0)
                    throw new InvalidOperationException("Ctrl+Space with only a decoration selected did not start at tile zero.");
                _smokePlaybackStage++;
                break;
            case 45:
                if (_playbackView != PlaybackView.Edit || !_inspectDecoration || _floor != _smokeFloor ||
                    _decoration != _smokeSelectedDecoration || _selectedDecorations.Count != 1 ||
                    !_selectedDecorations.Contains(_smokeSelectedDecoration!))
                    throw new InvalidOperationException("Escape did not restore the decoration selection.");
                Console.WriteLine("Verified decoration-only playback from tile zero and selection restoration.");
                goto case 46;
            case 46:
                Console.WriteLine("Verified selection and camera restoration, preview navigation, Shift+Escape, and Ctrl+Space from tile zero and with a modal open.");
                Console.WriteLine("Verified incremental timing, mixer, tile and event changes, undo and rate changes without reloading.");
                Console.WriteLine($"Verified tile editing, tiles-only mode, coarse/fine Ctrl+wheel, plain wheel, Space, native and Web Ctrl+Space routing, half speed, chart pitch preservation and {L.Language.Name} resources." +
                    (_smokeDecorationIndex >= 0 ? " Verified the rendered image quad." : ""));
                _allowClose = true;
                Close();
                break;
        }
    }

    private void VerifySmokePitch(LevelRenderer renderer, double expected)
    {
        if (!renderer.IsPlaying || renderer.Level!.GetSetting<double>("pitch") != expected ||
            Number(_document.Settings["pitch"], 100) != 125)
            throw new InvalidOperationException("Playback speed did not compose with chart pitch correctly.");
    }

    private static void VerifyClipboard()
    {
        EditorDocument doc = new();
        int before = doc.LastFloor;
        doc.Add(new Newtonsoft.Json.Linq.JObject { ["floor"] = 2, ["eventType"] = "Twirl" }, false);
        doc.Add(new Newtonsoft.Json.Linq.JObject { ["floor"] = 1, ["eventType"] = "AddDecoration" }, true);
        var copy = doc.CopyTiles(1, 2);
        doc.PasteTiles(before, copy, includeDecorations: true);
        if (doc.LastFloor != before + 2 || doc.Events(before + 2).Count != 1 || doc.Decorations.Count != 2)
            throw new InvalidOperationException("Clipboard contents were not pasted at the expected floors.");
        doc.DeleteTiles(before + 1, before + 2);
        doc.Undo();
        if (doc.Events(before + 2).Count != 1 || doc.LastFloor != before + 2)
            throw new InvalidOperationException("Tile range deletion did not undo atomically.");
        if (L.T("Settings") != (L.IsChinese ? "\u5173\u5361\u8bbe\u7f6e" : "Level settings"))
            throw new InvalidOperationException("The editor resources did not load in the requested language.");
    }
}
