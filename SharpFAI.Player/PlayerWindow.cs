using ImGui4NET;
using ImGui4NET.Backend.OpenGL3;
using NativeFileDialog;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SharpFAI.Player.Engine;
using System.Diagnostics;

namespace SharpFAI.Player;

internal sealed unsafe partial class PlayerWindow : GameWindow
{
    private ImGuiContext* _context;
    private WindowInputMethod? _inputMethod;
    private readonly Dictionary<Keys, ImGuiKey> _keys = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly LevelRenderer _renderer = new();
    private readonly LevelPanel _levelPanel = new();
    private readonly MenuBarFrameRate _menuBarFrameRate = new();
    private readonly VideoRenderWindow _videoRenderWindow = new();
    private readonly AudioCalibrationWindow _audioCalibration = new();
    private bool _showDemo;

    // View drag state
    private bool _panning;
    private Vector2 _lastMousePosition;

    /// <remarks>Screen position at left-button-down, to tell a click apart from a pan drag.</remarks>
    private Vector2 _pressPosition;
    private bool _pressPending;

    /// <summary>Pixel threshold separating a click from a pan.</summary>
    private const float ClickSlop = 4f;

    /// <summary>Level path from the command line, loaded on startup.</summary>
    public string? PendingLevelPath { get; set; }

    /// <summary>
    /// Command-line capture request: write a PNG here once the level has loaded and settled, then
    /// close. Set through <c>--shot</c> so a rendering problem can be checked without a screenshot.
    /// </summary>
    public string? CapturePath { get; set; }

    /// <summary>Zoom to apply before capturing; 0 keeps the automatic framing.</summary>
    public float CaptureZoom { get; set; }

    /// <summary>Floor index to select (and centre the camera on) before capturing; -1 keeps none.</summary>
    public int CaptureSelectFloor { get; set; } = -1;

    /// <summary>
    /// Music position (seconds) to seek to before capturing; negative keeps the level's start.
    /// Set through <c>--time</c> so a specific moment of a chart can be inspected.
    /// </summary>
    public double CaptureTime { get; set; } = -1;

    public bool SkipAudioFromCommandLine { get; set; }
    public string? FireColorFromCommandLine { get; set; }
    public string? IceColorFromCommandLine { get; set; }
    internal int CommandExitCode { get; private set; }

    /// <summary>Frames still to render before the capture fires; -1 means "not armed yet".</summary>
    private int _captureCountdown = -1;

    private bool _captureDone;

    public PlayerWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _videoExport = new VideoExportSession(_renderer);

        // 手打模式下整个键盘都是判定键，所以「按住」状态要从所有按键事件里维护：
        // KeyUp 必须到达 PlayInput，否则自动重复过滤会失效。
        // In manual play the whole keyboard is a hit key, so the held-key bookkeeping needs every
        // event: KeyUp must reach PlayInput or the auto-repeat filter stops working.
        KeyDown += e =>
        {
            if (_context == null)
                return;

            if (_settingsWindow.IsOpen)
            {
                FeedKey(e.Key, true);
                return;
            }

            if (ApplicationWindowModeController.IsToggleShortcut(e))
            {
                if (!e.IsRepeat)
                    _windowMode.RequestToggle();
                return;
            }

            if (_audioCalibration.IsMeasuring && e.Key == Keys.Space)
            {
                if (!e.IsRepeat)
                    _audioCalibration.Tap(_renderer);
                return;
            }

            FeedKey(e.Key, true);
            if (ExportRequest is not null || _audioCalibration.IsOpen || e.Key is Keys.LeftAlt or Keys.RightAlt)
                return;

            if (_renderer.ManualPlayEnabled && _renderer.PlaySession.Failed)
            {
                if (!e.IsRepeat && e.Key != Keys.Unknown)
                    _renderer.Play();
                return;
            }

            if (ImGui.GetIO()->WantCaptureKeyboard != 0)
            {
                return;
            }

            if (_renderer.ManualPlayEnabled)
            {
                if (e.IsRepeat)
                    return;

                // 进行中的手打可按 Esc 退出。
                if (e.Key == Keys.Escape)
                {
                    _renderer.SetManualPlay(false);
                    return;
                }

                if (!_renderer.IsPlaying && e.Key == Keys.Enter)
                    _renderer.Play();
                else
                    _renderer.PlayInput.Push(e.Key, pressed: true, _clock.Elapsed.TotalSeconds);
                return;
            }

            if (e.Key == Keys.Space)
            {
                _renderer.TogglePlay();
            }
            else if (e.Key == Keys.R)
            {
                _renderer.Restart();
            }
            else if (e.Key == Keys.P)
            {
                _renderer.TogglePlay();
            }
            else if (e.Key == Keys.M)
            {
                _renderer.SetManualPlay(true);
            }
        };

        KeyUp += e =>
        {
            if (_context == null)
                return;

            FeedKey(e.Key, false);
            if (!_renderer.ManualPlayEnabled)
            {
                return;
            }

            // 只用来解除「按住」标记
            _renderer.PlayInput.Push(e.Key, pressed: false, _clock.Elapsed.TotalSeconds);
        };

        MouseDown += e => BeginPan(e.Button);
        MouseUp += e => EndPan(e.Button);
        MouseWheel += ZoomWithMouseWheel;
    }

    /// <summary>Start panning when ImGui is not capturing the mouse.</summary>
    private void BeginPan(MouseButton button)
    {
        if (_context == null || ExportRequest is not null || _settingsWindow.IsOpen)
            return;

        if (_renderer.ManualPlayEnabled && _renderer.PlaySession.Failed &&
            (button is MouseButton.Left or MouseButton.Right) && ImGui.GetIO()->WantCaptureMouse == 0)
        {
            _renderer.Play();
            return;
        }

        if (_renderer.ManualPlayEnabled && _renderer.IsPlaying)
        {
            if (ImGui.GetIO()->WantCaptureMouse == 0)
                _renderer.PlayInput.Push(button, pressed: true, _clock.Elapsed.TotalSeconds);
            return;
        }

        if (button != MouseButton.Left)
        {
            return;
        }

        ImGuiIO* io = ImGui.GetIO();
        if (io->WantCaptureMouse != 0)
        {
            return;
        }

        _panning = true;
        _pressPending = true;
        _pressPosition = new Vector2(MouseState.X, MouseState.Y);
        _lastMousePosition = _pressPosition;
    }

    /// <summary>
    /// Left-button up: treat a near-stationary press as a click, pick the floor under the cursor, and
    /// clear the selection when the click lands on empty space.
    /// </summary>
    private void EndPan(MouseButton button)
    {
        if (_renderer.ManualPlayEnabled)
            _renderer.PlayInput.Push(button, pressed: false, _clock.Elapsed.TotalSeconds);

        if (button != MouseButton.Left)
        {
            return;
        }

        bool wasPanning = _panning;
        _panning = false;

        if (!_pressPending || !wasPanning)
        {
            _pressPending = false;
            return;
        }

        _pressPending = false;

        Vector2 current = new(MouseState.X, MouseState.Y);

        // Past the threshold this was a pan, so keep the selection
        if ((current - _pressPosition).LengthSquared > ClickSlop * ClickSlop)
        {
            return;
        }

        if (ImGui.GetIO()->WantCaptureMouse != 0)
        {
            return;
        }

        // Ignore click-to-select while playback is running
        if (_renderer.IsPlaying)
        {
            return;
        }

        _renderer.SelectFloor(_renderer.PickFloor(current));
    }

    /// <summary>Zoom the view with the wheel.</summary>
    private void ZoomWithMouseWheel(MouseWheelEventArgs e)
    {
        if (_context == null || ExportRequest is not null || _settingsWindow.IsOpen || ImGui.GetIO()->WantCaptureMouse != 0)
        {
            return;
        }

        _renderer.Zoom(e.OffsetY, new Vector2(MouseState.X, MouseState.Y));
    }

    /// <summary>Pan the view while dragging.</summary>
    private void UpdatePan()
    {
        if (!_panning)
        {
            return;
        }

        Vector2 current = new(MouseState.X, MouseState.Y);
        Vector2 delta = current - _lastMousePosition;
        _lastMousePosition = current;

        if (delta.LengthSquared < 1e-6f)
        {
            return;
        }

        // Stop panning if the button was released outside the window
        if (!MouseState.IsButtonDown(MouseButton.Left))
        {
            _panning = false;
            return;
        }

        _renderer.Pan(delta);
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        if (ExportRequest is null)
        {
            InitializeImGui();
            _inputMethod = WindowInputMethod.Create(WindowPtr);
        }

        LoadApplicationSettings();
        UpdateWindowTitle();
        _renderer.Resize(ClientSize.X, ClientSize.Y);

        if (!string.IsNullOrWhiteSpace(PendingLevelPath))
        {
            OpenLevel(PendingLevelPath);
            PendingLevelPath = null;
        }
    }

    private void InitializeImGui()
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);

        ImGuiIO* io = ImGui.GetIO();
        io->IniFilename = null;
        io->LogFilename = null;
        if (ImGui_ImplOpenGL3.Init("#version 330") == 0)
            throw new InvalidOperationException("ImGui_ImplOpenGL3.Init 失败");

        ImGui.StyleColorsDark();
        ApplicationFont.LoadInterfaceFont(ImGui.GetIO(), 16f);
        BuildKeyMap();
        TextInput += e => ImGuiIO.AddInputCharacter(ImGui.GetIO(), (uint)e.Unicode);
        MouseWheel += e => ImGuiIO.AddMouseWheelEvent(ImGui.GetIO(), e.OffsetX, e.OffsetY);
        MouseMove += _ => FeedMouse();
        MouseDown += _ => FeedMouse();
        MouseUp += _ => FeedMouse();
    }

    protected override void OnUnload()
    {
        _displayFrameLimit.Dispose();
        _inputMethod?.Dispose();
        _videoExport.Dispose();
        _audioCalibration.Dispose();
        _renderer.Dispose();

        if (_context != null)
        {
            ImGui_ImplOpenGL3.Shutdown();
            ImGui.DestroyContext(_context);
            _context = null;
        }

        base.OnUnload();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);

        // 导出期间视口由导出流程自己管理，不能被窗口尺寸覆盖
        // During export the viewport belongs to the export loop and must not follow the window
        if (ExportRequest is null)
        {
            _renderer.Resize(ClientSize.X, ClientSize.Y);
        }
    }

    /// <summary>
    /// 失去焦点时清空「按住」状态：否则切回窗口后的第一次按键会被误判成自动重复而丢掉。
    /// Clears the held-key state on focus loss; otherwise the first key after returning would be
    /// mistaken for an auto-repeat and dropped.
    /// </summary>
    protected override void OnFocusedChanged(FocusedChangedEventArgs e)
    {
        base.OnFocusedChanged(e);

        if (_context != null)
            ImGuiIO.AddFocusEvent(ImGui.GetIO(), (byte)(e.IsFocused ? 1 : 0));
        if (!e.IsFocused)
        {
            _inputMethod?.Restore();
            _renderer.PlayInput.ReleaseAll(_clock.Elapsed.TotalSeconds);
        }
    }

    /// <summary>Write the live GL renderer into the title.</summary>
    private void UpdateWindowTitle()
    {
        string renderer;
        try
        {
            renderer = GL.GetString(StringName.Renderer)?.Trim() ?? "Unknown GPU";
            if (renderer.Length == 0)
            {
                renderer = "Unknown GPU";
            }
        }
        catch (Exception ex)
        {
            renderer = $"GPU 查询失败: {ex.Message}";
        }

        Title = $"SharpFAI Player — {renderer}";
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);

        ApplyPendingApplicationSettings();

        // 命令行导出接管整帧：不画 ImGui，一次回调里尽量多渲几帧
        // A command-line export owns the frame: no ImGui, and as many frames per callback as it can
        if (ExportRequest is not null && !_isInteractiveExport)
        {
            // 后台加载靠 Tick 推进，导出时也必须继续泵它，否则关卡永远加载不完
            // The background load is pumped by Tick, so export has to keep pumping it or the level
            // never finishes loading
            _renderer.Tick(_clock.Elapsed.TotalSeconds);
            RenderExportFrame();

            return;
        }

        _renderer.Tick(_clock.Elapsed.TotalSeconds);
        bool rendering = ExportRequest is not null;
        if (rendering)
        {
            RenderExportFrame();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        else
            UpdatePan();

        _menuBarFrameRate.Sample(e.Time);
        ImDrawData* drawData = BuildOverlay((float)e.Time, rendering);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        Vector4 background = _renderer.BackgroundColor;
        GL.ClearColor(background.X, background.Y, background.Z, background.W);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (!rendering)
        {
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            _renderer.RenderAfterClear(background);
        }

        ImGui_ImplOpenGL3.RenderDrawData(drawData);
        UpdateCapture();
        SwapBuffers();
    }

    private ImDrawData* BuildOverlay(float lastDeltaTime, bool rendering)
    {
        ImGuiIO* io = ImGui.GetIO();
        io->DeltaTime = lastDeltaTime;
        io->DisplaySize = new ImVec2 { x = ClientSize.X, y = ClientSize.Y };
        io->DisplayFramebufferScale = new ImVec2 { x = 1f, y = 1f };

        ImGui_ImplOpenGL3.NewFrame();
        _settingsWindow.BeginFrame(io);
        ImGui.NewFrame();

        float menuBottom = DrawMenuBar(rendering);
        _levelPanel.Draw(_renderer, menuBottom, rendering, _audioCalibration);
        _videoRenderWindow.Draw(_videoExport.Status, RequestVideoExport, CancelVideoExport,
            _renderer.Level is not null && !_renderer.IsLoading ? null : "请先加载关卡");
        _audioCalibration.Draw(_renderer);
        _settingsWindow.Draw(chinese: true, editor: false, ApplicationSettings.PlayerDefaults, TryApplyApplicationSettings);

        if (_showDemo)
        {
            bool open = true;
            ImGui.ShowDemoWindow(ref open);
            _showDemo = open;
        }

        if (ExportRequest is null && !_audioCalibration.IsOpen)
            ManualPlayHud.Draw(_renderer);

        _inputMethod?.Update(IsFocused, !rendering && !_audioCalibration.IsOpen && _renderer.Level is not null &&
            (_renderer.IsPlaying || _renderer.ManualPlayEnabled || _renderer.SelectedFloor >= 0));
        ImGui.Render();
        return ImGui.GetDrawData();
    }

    /// <summary>
    /// Drive the command-line capture: wait for the load to finish, give the renderer a few frames to
    /// settle, apply the requested zoom, then write the PNG and close the window.
    /// </summary>
    private void UpdateCapture()
    {
        if (CapturePath is null || _captureDone)
        {
            return;
        }

        if (_renderer.LoadError is { } error)
        {
            Console.Error.WriteLine($"capture failed: {error}");
            CommandExitCode = 1;
            Close();
            return;
        }

        if (_renderer.IsLoading || _renderer.Level is null)
        {
            return;
        }

        if (_captureCountdown < 0)
        {
            _captureCountdown = 30;

            if (CaptureTime >= 0)
            {
                // Use the playback track state at this exact time, not the static editor track.
                _renderer.SeekForOfflineRender(Math.Min(CaptureTime, _renderer.Duration));
            }

            if (CaptureSelectFloor >= 0)
            {
                _renderer.SelectFloor(CaptureSelectFloor);

                // Centre the camera on the selected tile so the zoom actually shows it
                if (_renderer.TryGetFloorCenter(CaptureSelectFloor, out Vector2 centre))
                {
                    _renderer.Camera.Position = centre;
                    _renderer.Camera.MarkDirty();
                }
            }

            if (CaptureZoom > 0f)
            {
                _renderer.Camera.Zoom = CaptureZoom;
                _renderer.Camera.MarkDirty();
            }

            return;
        }

        if (_captureCountdown-- > 0)
        {
            return;
        }

        _captureDone = true;

        try
        {
            FrameCapture.Save(CapturePath, FramebufferSize.X, FramebufferSize.Y);
            Console.Out.WriteLine($"captured {FramebufferSize.X}x{FramebufferSize.Y} -> {CapturePath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture failed: {ex.Message}");
            CommandExitCode = 1;
        }

        Close();
    }

    private float DrawMenuBar(bool rendering)
    {
        if (ImGui.BeginMainMenuBar() == 0)
            return ImGui.GetMainViewport()->WorkPos.y;

        float bottom = ImGui.GetWindowPos().y + ImGui.GetWindowHeight();
        if (ImGui.BeginMenu("文件") != 0)
        {
            if (rendering)
                ImGui.BeginDisabled();
            if (ImGui.MenuItem("打开...", "Ctrl+O") != 0)
            {
                OpenLevel(ShowOpenDialog());
            }

            if (ImGui.MenuItem("关闭关卡") != 0 && _renderer.LevelPath is not null)
            {
                _renderer.Unload();
            }
            if (rendering)
                ImGui.EndDisabled();

            ImGui.Separator();

            if (ImGui.MenuItem("退出", "Alt+F4") != 0)
            {
                Close();
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("视图") != 0)
        {
            if (ImGui.MenuItem("全屏", "Alt+Enter", (byte)(_windowMode.IsFullscreen ? 1 : 0)) != 0)
                _windowMode.RequestToggle();

            if (rendering)
                ImGui.BeginDisabled();
            if (ImGui.MenuItem("音画校准…") != 0)
                _audioCalibration.Open();
            if (rendering)
                ImGui.EndDisabled();

            byte selected = (byte)(_showDemo ? 1 : 0);
            if (ImGui.MenuItem("ImGui 演示窗口", null, selected) != 0)
            {
                _showDemo = !_showDemo;
            }

            ImGui.EndMenu();
        }

        if (ImGui.MenuItem("渲染") != 0)
            _videoRenderWindow.Open();

        DrawSettingsMenuItem(rendering);
        _menuBarFrameRate.Draw(rendering ? "视频渲染中…" : _renderer.Status, _applicationSettings.ShowFrameRate);

        ImGui.EndMainMenuBar();
        return bottom;
    }

    private void OpenLevel(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // Loading happens in the background, so only kick it off; the UI stays responsive
        _renderer.Load(path);
    }

    private static string? ShowOpenDialog()
    {
        IFileDialog? dialog = NativeFileDialog.NativeFileDialog.GetFileDialog();
        if (dialog == null)
        {
            return null;
        }

        OpenFileFilter filter = new()
        {
            Filter = new Dictionary<string, List<string>>
            {
                ["ADOFAI 关卡"] = ["adofai"],
                ["JSON 文件"] = ["json"],
            },
            IncludeAllFiles = true,
        };

        string[]? files = dialog.OpenFile("打开关卡", string.Empty, filter);
        return files is { Length: > 0 } ? files[0] : null;
    }

    private void FeedMouse()
    {
        ImGuiIO* io = ImGui.GetIO();
        MouseState mouse = MouseState;
        ImGuiIO.AddMousePosEvent(io, mouse.X, mouse.Y);
        ImGuiIO.AddMouseButtonEvent(io, 0, (byte)(mouse.IsButtonDown(MouseButton.Left) ? 1 : 0));
        ImGuiIO.AddMouseButtonEvent(io, 1, (byte)(mouse.IsButtonDown(MouseButton.Right) ? 1 : 0));
        ImGuiIO.AddMouseButtonEvent(io, 2, (byte)(mouse.IsButtonDown(MouseButton.Middle) ? 1 : 0));
    }

    private void FeedKey(Keys key, bool down)
    {
        if (_keys.TryGetValue(key, out ImGuiKey mapped))
        {
            ImGuiIO.AddKeyEvent(ImGui.GetIO(), mapped, (byte)(down ? 1 : 0));
        }
    }

    private void BuildKeyMap()
    {
        void Add(Keys key, ImGuiKey imguiKey) => _keys[key] = imguiKey;

        Add(Keys.Tab, ImGuiKey.ImGuiKey_Tab);
        Add(Keys.Left, ImGuiKey.ImGuiKey_LeftArrow);
        Add(Keys.Right, ImGuiKey.ImGuiKey_RightArrow);
        Add(Keys.Up, ImGuiKey.ImGuiKey_UpArrow);
        Add(Keys.Down, ImGuiKey.ImGuiKey_DownArrow);
        Add(Keys.PageUp, ImGuiKey.ImGuiKey_PageUp);
        Add(Keys.PageDown, ImGuiKey.ImGuiKey_PageDown);
        Add(Keys.Home, ImGuiKey.ImGuiKey_Home);
        Add(Keys.End, ImGuiKey.ImGuiKey_End);
        Add(Keys.Insert, ImGuiKey.ImGuiKey_Insert);
        Add(Keys.Delete, ImGuiKey.ImGuiKey_Delete);
        Add(Keys.Backspace, ImGuiKey.ImGuiKey_Backspace);
        Add(Keys.Space, ImGuiKey.ImGuiKey_Space);
        Add(Keys.Enter, ImGuiKey.ImGuiKey_Enter);
        Add(Keys.Escape, ImGuiKey.ImGuiKey_Escape);
        Add(Keys.LeftControl, ImGuiKey.ImGuiKey_LeftCtrl);
        Add(Keys.LeftShift, ImGuiKey.ImGuiKey_LeftShift);
        Add(Keys.LeftAlt, ImGuiKey.ImGuiKey_LeftAlt);
        Add(Keys.LeftSuper, ImGuiKey.ImGuiKey_LeftSuper);
        Add(Keys.RightControl, ImGuiKey.ImGuiKey_RightCtrl);
        Add(Keys.RightShift, ImGuiKey.ImGuiKey_RightShift);
        Add(Keys.RightAlt, ImGuiKey.ImGuiKey_RightAlt);
        Add(Keys.RightSuper, ImGuiKey.ImGuiKey_RightSuper);
        Add(Keys.Menu, ImGuiKey.ImGuiKey_Menu);
        Add(Keys.Apostrophe, ImGuiKey.ImGuiKey_Apostrophe);
        Add(Keys.Comma, ImGuiKey.ImGuiKey_Comma);
        Add(Keys.Minus, ImGuiKey.ImGuiKey_Minus);
        Add(Keys.Period, ImGuiKey.ImGuiKey_Period);
        Add(Keys.Slash, ImGuiKey.ImGuiKey_Slash);
        Add(Keys.Semicolon, ImGuiKey.ImGuiKey_Semicolon);
        Add(Keys.Equal, ImGuiKey.ImGuiKey_Equal);
        Add(Keys.LeftBracket, ImGuiKey.ImGuiKey_LeftBracket);
        Add(Keys.Backslash, ImGuiKey.ImGuiKey_Backslash);
        Add(Keys.RightBracket, ImGuiKey.ImGuiKey_RightBracket);
        Add(Keys.GraveAccent, ImGuiKey.ImGuiKey_GraveAccent);

        Keys[] digits = [Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7, Keys.D8, Keys.D9];
        ImGuiKey[] digitKeys =
        [
            ImGuiKey.ImGuiKey_0, ImGuiKey.ImGuiKey_1, ImGuiKey.ImGuiKey_2, ImGuiKey.ImGuiKey_3, ImGuiKey.ImGuiKey_4,
            ImGuiKey.ImGuiKey_5, ImGuiKey.ImGuiKey_6, ImGuiKey.ImGuiKey_7, ImGuiKey.ImGuiKey_8, ImGuiKey.ImGuiKey_9,
        ];
        for (int i = 0; i < digits.Length; i++)
        {
            Add(digits[i], digitKeys[i]);
        }

        for (int i = 0; i < 26; i++)
        {
            Add((Keys)((int)Keys.A + i), (ImGuiKey)((int)ImGuiKey.ImGuiKey_A + i));
        }

        for (int i = 0; i < 12; i++)
        {
            Add((Keys)((int)Keys.F1 + i), (ImGuiKey)((int)ImGuiKey.ImGuiKey_F1 + i));
        }
    }

}
