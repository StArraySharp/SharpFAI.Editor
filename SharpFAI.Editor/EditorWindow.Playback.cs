using System.Diagnostics;
using ImGui4NET;
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

    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
    private LevelRenderer? _playback;
    private PlaybackView _playbackView;
    private PlaybackRevision? _requestedRevision;
    private PlaybackRevision? _displayedRevision;
    private PlaybackRevision? _observedRevision;
    private PlaybackRevision? _effectRevision;
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
    private (Vector2 Position, float Zoom)? _savedEditCamera;
    private bool _playbackHovered;
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
    private double _playbackTickTime;
    private double _smokeRateWarmupUntil;
    private (double WallTime, double ChartTime)? _smokeRateSample;

    private PlaybackRevision CurrentPlaybackRevision => new(_document, _document.ChangeVersion,
        _document.PlaybackPath, _appliedPlaybackSpeed);
    private bool HasCurrentPlayback => _playback is { IsLoading: false, LoadError: null, Level: not null }
                                      && _requestedRevision == CurrentPlaybackRevision;

    private static void InitializePlayback() => Shader.SourceResolver = ShaderLibrary.ResolveForEngine;

    private void SwitchPlaybackView(PlaybackView view, PlaybackRate rate = PlaybackRate.Normal)
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
            _playbackFloor = _floor;
            _centerEditSelection = true;
            _playback?.Pause();
            _playback?.SetManualPlay(false);
            return;
        }

        if (previous == PlaybackView.Edit && _playback is { Level: not null } editRenderer)
            _savedEditCamera = (editRenderer.Camera.Position, editRenderer.Camera.Zoom);
        if (previous == PlaybackView.Edit || view == PlaybackView.Preview)
            _playbackFloor = _floor;
        else if (view == PlaybackView.Manual && _playbackFloor is null && HasCurrentPlayback)
            _playbackFloor = _playback!.CurrentIndex;

        if (_playback is { LoadError: not null })
            _requestedRevision = null;
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
        LevelRenderer renderer = _playback ??= new LevelRenderer();
        var previousLevel = renderer.Level;
        if (editing && renderer.IsLoading && previousLevel is not null)
            _savedEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
        renderer.Tick(now);
        if (_smokeTest) _playbackTickTime = now;
        PlaybackRevision? failed = null;
        if (_effectRevision is { } updating)
        {
            PlaybackRevision latest = CurrentPlaybackRevision;
            bool obsolete = updating.Document != latest.Document || updating.SourcePath != latest.SourcePath ||
                updating.Speed != latest.Speed || !_document.HasOnlyEffectChangesSince(updating.Version);
            if (obsolete)
                renderer.CancelEditorEffectUpdate();
            else
            {
                var status = renderer.FinishEditorEffectUpdate();
                if (status == LevelRenderer.EditorEffectStatus.Pending)
                    return;
                if (status == LevelRenderer.EditorEffectStatus.Applied)
                    AcceptEffectRevision(updating);
                else
                    failed = updating;
            }
            _effectRevision = null;
        }
        if (renderer.Level is { } level && !ReferenceEquals(previousLevel, level))
        {
            _displayedRevision = _requestedRevision;
            if (_displayedRevision is { } displayed && displayed.Document == _document)
            {
                _pendingTiles.RemoveAll(tile => tile.Version <= displayed.Version);
                _document.ForgetEffectChangesThrough(displayed.Version);
            }
        }
        PlaybackRevision target = CurrentPlaybackRevision;
        if (_observedRevision != target)
        {
            _observedRevision = target;
            _lastPreviewEdit = now;
        }

        if (_requestedRevision != target)
        {
            if (!renderer.IsLoading && failed != target && _displayedRevision is { } current &&
                current.Document == target.Document && current.SourcePath == target.SourcePath &&
                current.Speed == target.Speed &&
                renderer.BeginEditorEffectUpdate(_document.EffectChangesSince(current.Version)))
            {
                var status = renderer.FinishEditorEffectUpdate();
                if (status == LevelRenderer.EditorEffectStatus.Pending)
                {
                    _effectRevision = target;
                    return;
                }
                if (status == LevelRenderer.EditorEffectStatus.Applied)
                {
                    AcceptEffectRevision(target);
                    goto PlaybackReady;
                }
            }
            bool debounce = _requestedRevision?.Document == _document && !_startPlayback;
            double delay = editing && _pendingTiles.Count > 0 ? 0.035 : 0.2;
            if (renderer.IsLoading || (debounce && now - _lastPreviewEdit < delay))
                return;

            if (!editing && _requestedRevision?.Document == _document && renderer.Level is not null)
            {
                _playbackFloor ??= renderer.CurrentIndex;
                _startPlayback |= renderer.IsPlaying;
            }
            else
                _playbackFloor ??= _floor;

            if (editing && _requestedRevision?.Document == _document && renderer.Level is not null)
                _savedEditCamera = (renderer.Camera.Position, renderer.Camera.Zoom);
            var snapshot = _document.SnapshotForPlayback();
            if (target.Speed != 1)
                snapshot.PutSetting("pitch", Number(_document.Settings["pitch"], 100) * target.Speed);
            renderer.Load(snapshot, target.SourcePath,
                keepEditorScene: editing && _displayedRevision?.Document == _document);
            _requestedRevision = target;
            return;
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

    private void AcceptEffectRevision(PlaybackRevision revision)
    {
        _requestedRevision = _displayedRevision = revision;
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
                renderer.Resize(width, height);
            }

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

    private void FeedPlaybackKey(KeyboardKeyEventArgs args, bool pressed)
    {
        if (pressed && args.Key == Keys.Escape && _playbackView != PlaybackView.Edit)
        {
            if (args.Shift && _playback?.Level is not null)
                SelectFloor(_playback.CurrentIndex, true);
            SwitchPlaybackView(PlaybackView.Edit);
            return;
        }
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
            pressed && !_playbackHovered)
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
                break;
            case 13 when _playbackView == PlaybackView.Edit:
                FeedEditorWheel(io, 0, -2, ctrl: true, shift: false);
                FeedKey(new KeyboardKeyEventArgs(Keys.Space, 0, KeyModifiers.Control, false), true);
                FeedKey(new KeyboardKeyEventArgs(Keys.Space, 0, 0, false), false);
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
                break;
        }
    }

    private void VerifyPlaybackSmokeTest()
    {
        if (_playback?.LoadError is { } error)
            throw new InvalidOperationException(error);
        if (_playbackClock.Elapsed.TotalSeconds > _smokeDeadline)
            throw new TimeoutException("Player preview sync timed out.");
        if (!HasCurrentPlayback || !_playbackFrameAvailable || _playback is not { } renderer)
            return;

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
                SwitchPlaybackView(PlaybackView.Manual);
                _smokePlaybackStage++;
                break;
            case 3:
                if (!renderer.IsPlaying || !renderer.ManualPlayEnabled)
                    throw new InvalidOperationException("Manual play did not start.");
                SwitchPlaybackView(PlaybackView.Edit);
                if (renderer.IsPlaying || renderer.ManualPlayEnabled)
                    throw new InvalidOperationException("Manual play did not stop on return to the editor.");
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
                _smokePlaybackStage++;
                break;
            case 13:
                if (_playbackView != PlaybackView.Auto || _playbackSpeed != 55)
                    throw new InvalidOperationException("Ctrl+Space did not start playback with the same-frame wheel adjustment.");
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
                _smokePlaybackStage++;
                break;
            case 16:
                if (_playbackView == PlaybackView.Auto && _startPlayback) return;
                VerifySmokePitch(renderer, 125);
                _smokePlaybackStage++;
                break;
            case 17:
                SwitchPlaybackView(PlaybackView.Edit);
                Console.WriteLine($"Verified tile editing, tiles-only mode, coarse/fine Ctrl+wheel, plain wheel, Space, Ctrl+Space, half speed, chart pitch preservation and {L.Language.Name} resources." +
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
