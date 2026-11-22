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

internal sealed unsafe class PlayerWindow : GameWindow
{
    private ImGuiContext* _context;
    private readonly Dictionary<Keys, ImGuiKey> _keys = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly LevelRenderer _renderer = new();
    private readonly LevelPanel _levelPanel = new();
    private readonly VideoRenderWindow _videoRenderWindow = new();
    private readonly AudioCalibrationWindow _audioCalibration = new();
    private bool _showDemo;
    private bool _fullscreenTogglePending;
    private WindowState _windowedState = WindowState.Normal;

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

    /// <summary>Skip hit-sound synthesis on load; set through <c>--no-hitsound</c>.</summary>
    public bool SkipHitsound
    {
        get => _renderer.SkipHitsound;
        set => _renderer.SkipHitsound = value;
    }

    /// <summary>
    /// Command-line shader conversion request: (game shader root, output directory). Runs once a GL
    /// context is current, then closes the window.
    /// </summary>
    public (string ShaderRoot, string OutputDirectory)? PendingShaderConversion { get; set; }

    /// <summary>
    /// Command-line shader verification request: a directory of <c>.vert</c>/<c>.frag</c> pairs.
    /// Runs once a GL context is current, then closes the window.
    /// </summary>
    public string? PendingShaderVerification { get; set; }

    /// <summary>Frames still to render before the capture fires; -1 means "not armed yet".</summary>
    private int _captureCountdown = -1;

    private bool _captureDone;

    /// <summary>命令行视频导出请求；非 null 时逐帧渲染并合成视频。</summary>
    public VideoExporter.Request? ExportRequest { get; set; }

    /// <summary>导出过程中的状态字段。</summary>
    private bool _exportArmed;
    private int _exportFrame;
    private int _exportFrameCount;
    private double _exportStartedAt;
    private int _exportSettleFrames;
    private int _exportWidth;
    private int _exportHeight;
    private int _exportFps;
    private bool _exportWaitingLogged;

    public PlayerWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        // 手打模式下整个键盘都是判定键，所以「按住」状态要从所有按键事件里维护：
        // KeyUp 必须到达 PlayInput，否则自动重复过滤会失效。
        // In manual play the whole keyboard is a hit key, so the held-key bookkeeping needs every
        // event: KeyUp must reach PlayInput or the auto-repeat filter stops working.
        KeyDown += e =>
        {
            if (_context == null)
                return;

            if (e.Alt && e.Key is (Keys.Enter or Keys.KeyPadEnter))
            {
                if (!e.IsRepeat)
                    _fullscreenTogglePending = !_fullscreenTogglePending;
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
        if (_context == null || ExportRequest is not null)
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
        if (_context == null || ExportRequest is not null || ImGui.GetIO()->WantCaptureMouse != 0)
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
            InitializeImGui();

        UpdateWindowTitle();
        _renderer.Resize(ClientSize.X, ClientSize.Y);

        if (!string.IsNullOrWhiteSpace(PendingLevelPath))
        {
            OpenLevel(PendingLevelPath);
            PendingLevelPath = null;
        }

        // 着色器转换需要当前 GL 上下文，所以放在 OnLoad 末尾；完成后直接关闭窗口
        // Shader conversion needs a current GL context, so it runs last in OnLoad and then closes
        if (PendingShaderConversion is { } conversion)
        {
            PendingShaderConversion = null;
            RunShaderConversion(conversion.ShaderRoot, conversion.OutputDirectory);
            return;
        }

        if (PendingShaderVerification is { } verification)
        {
            PendingShaderVerification = null;
            RunShaderVerification(verification);
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
        LoadFont();
        BuildKeyMap();
        TextInput += e => ImGuiIO.AddInputCharacter(ImGui.GetIO(), (uint)e.Unicode);
        MouseWheel += e => ImGuiIO.AddMouseWheelEvent(ImGui.GetIO(), e.OffsetX, e.OffsetY);
        MouseMove += _ => FeedMouse();
        MouseDown += _ => FeedMouse();
        MouseUp += _ => FeedMouse();
    }

    /// <summary>
    /// 校验目录下所有着色器对能否被引擎编译，结果打印到标准输出，然后退出。
    /// Verifies every shader pair under a directory compiles, prints the report, then exits.
    /// </summary>
    private void RunShaderVerification(string shaderDirectory)
    {
        try
        {
            ConversionReport report = ShaderConverter.VerifyDirectory(shaderDirectory);

            Console.Out.WriteLine(
                $"verify: {report.SuccessCount} compiled, {report.FailureCount} failed, " +
                $"{report.Entries.Count} total");

            foreach (ConversionEntry entry in report.Entries)
            {
                if (entry.Compiled)
                {
                    continue;
                }

                Console.Out.WriteLine($"FAIL {entry.File}");
                Console.Out.WriteLine("  " + (entry.Error ?? "unknown").Replace("\n", "\n  "));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shader verification failed: {ex.Message}");
        }

        Close();
    }

    /// <summary>
    /// 批量转换游戏 shader 并校验编译，结果打印到标准输出，然后退出。
    /// Converts the game's shaders, verifies each pair compiles, prints the report, then exits.
    /// </summary>
    private void RunShaderConversion(string shaderRoot, string outputDirectory)
    {
        try
        {
            ConversionReport report = ShaderConverter.ConvertGameShaders(shaderRoot, outputDirectory);

            Console.Out.WriteLine(
                $"shaders: {report.SuccessCount} compiled, {report.FailureCount} failed, " +
                $"{report.Entries.Count} total");

            foreach (ConversionEntry entry in report.Entries)
            {
                if (entry.Compiled)
                {
                    continue;
                }

                Console.Out.WriteLine($"FAIL {entry.File}");
                Console.Out.WriteLine("  " + (entry.Error ?? "unknown").Replace("\n", "\n  "));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shader conversion failed: {ex.Message}");
        }

        Close();
    }

    protected override void OnUnload()
    {
        ReleaseVideoResources();
        ReleaseOffscreenTarget();
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

        if (!e.IsFocused)
        {
            _renderer.PlayInput.ReleaseAll(_clock.Elapsed.TotalSeconds);
        }
    }

    /// <summary>命令行导出：整帧接管，把帧数渲满（受 <see cref="FramesPerCallback"/> 限制）。</summary>
    private void RenderExportFrame()
    {
        VideoExporter.Request request = ExportRequest!.Value;
        try
        {
            RenderExportSteps(FramesPerCallback);
        }
        catch (Exception ex) when (ExportRequest is not null)
        {
            FinishExport(request, ex);
        }
    }

    /// <summary>
    /// 推进至多 <paramref name="budget"/> 帧。渲染与编码都在这里完成，调用方负责绘制 UI。
    /// Advance up to `budget` frames; rendering and encoding happen here and the caller draws the UI.
    /// </summary>
    private void RenderExportSteps(int budget)
    {
        VideoExporter.Request request = ExportRequest!.Value;

        if (_renderer.IsLoading || _renderer.Level is null)
        {
            if (!_renderer.IsLoading)
                throw new InvalidOperationException(_renderer.LoadError ?? _renderer.Status);

            // 只在状态变化时打日志，避免刷屏
            // Log only on state change so the console does not flood
            if (!_exportWaitingLogged)
            {
                _exportWaitingLogged = true;
                Console.Out.WriteLine(
                    $"export waiting: loading={_renderer.IsLoading} level={(_renderer.Level is null ? "null" : "ready")}");
            }

            return;
        }

        if (!_exportArmed)
        {
            ArmExport(request);
        }

        // 加载后先丢几帧让纹理与状态稳定，避免第一帧读到空白
        // Drop a few frames after loading so textures and state settle; the first frame otherwise
        // reads back blank
        if (_exportSettleFrames > 0)
        {
            _exportSettleFrames--;

            return;
        }

        if (_exportFrame >= _exportFrameCount)
        {
            FinishExport(request);
            return;
        }

        VideoReadback readback = _videoReadback!;
        VideoFrameWriter writer = _videoWriter!;
        long batchStart = Stopwatch.GetTimestamp();
        while (budget-- > 0 && _exportFrame < _exportFrameCount && !writer.Failed)
        {
            // 画面与音频共用同一个时钟：第 n 帧就是 n / fps 秒
            // Picture and audio share one clock: frame n is exactly n / fps seconds
            double time = _exportFrame / (double)_exportFps * _renderer.PlaybackRate;

            long start = Stopwatch.GetTimestamp();
            RenderToOffscreen(_exportWidth, _exportHeight, time);
            _exportDrawTicks += Stopwatch.GetTimestamp() - start;

            start = Stopwatch.GetTimestamp();
            _videoColorConverter?.Pack(_exportColor);
            readback.Submit();
            _exportReadbackTicks += Stopwatch.GetTimestamp() - start;
            if (readback.Full)
                QueueVideoFrame(readback, writer);

            _exportFrame++;

            if (_exportFrame % 300 == 0 || _exportFrame == _exportFrameCount)
            {
                Console.Out.WriteLine($"export {_exportFrame}/{_exportFrameCount}");
            }

            if (_isInteractiveExport && Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds >= 12.0)
                break;
        }

        if (writer.Failed)
        {
            FinishExport(request);
            return;
        }

    }

    /// <summary>一次回调里渲染的帧数上限：够快又不会让窗口长时间无响应。</summary>
    private const int FramesPerCallback = 12;

    private VideoReadback? _videoReadback;
    private VideoColorConverter? _videoColorConverter;
    private VideoFrameWriter? _videoWriter;
    private string? _encoderAudioPath;
    private long _exportDrawTicks;
    private long _exportReadbackTicks;
    private long _exportQueueTicks;

    private void QueueVideoFrame(VideoReadback readback, VideoFrameWriter writer)
    {
        long start = Stopwatch.GetTimestamp();
        byte[] pixels = readback.Take();
        _exportReadbackTicks += Stopwatch.GetTimestamp() - start;

        start = Stopwatch.GetTimestamp();
        writer.Enqueue(pixels);
        _exportQueueTicks += Stopwatch.GetTimestamp() - start;
    }

    /// <summary>
    /// 把音乐与打击音混成一个临时 WAV，作为 ffmpeg 的第二路输入。
    /// Mix the music and hitsound into a temp WAV for ffmpeg's second input.
    /// </summary>
    /// <returns>WAV 路径；没有音频时返回 null。</returns>
    private string? WriteExportAudio()
    {
        (string? musicPath, Thrust.Audio.DecodedAudio? music, Thrust.Audio.DecodedAudio? hitsound) =
            _renderer.AudioSources;

        string path = Path.Combine(Path.GetTempPath(), $"sharpfai_audio_{Environment.ProcessId}.wav");

        if (music is not null || hitsound is not null)
        {
            _encoderAudioPath = path;
            VideoExporter.WriteMixedWav(
                music,
                hitsound,
                _renderer.MusicVolumeExport,
                _renderer.HitsoundVolumeExport,
                _exportFrameCount / (double)_exportFps,
                path,
                _renderer.PlaybackRate);

            Console.Out.WriteLine($"audio mixed -> {path}");
            return path;
        }

        // 解码结果缺失时退回源文件，仍由 ffmpeg 处理。但视频绝不能被当作音频输入：
        // 关卡目录里的 mp4（常见为背景视频）带着音轨，ffmpeg 会把它混进来。
        // Fall back to the source file when decoding was skipped; ffmpeg handles it. A video must
        // never be the audio input though: mp4 files in a level folder (usually the background
        // video) carry a track that ffmpeg would happily mix in.
        if (musicPath is not null && File.Exists(musicPath) && !VideoExporter.IsVideoPath(musicPath))
        {
            return musicPath;
        }

        return null;
    }

    private int _exportFbo;
    private int _exportColor;
    private int _exportFboWidth;
    private int _exportFboHeight;

    /// <summary>把一帧渲染到离屏 FBO；调用方负责读回像素 / Render one frame into the offscreen FBO.</summary>
    private void RenderToOffscreen(int width, int height, double time)
    {
        EnsureOffscreenTarget(width, height);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _exportFbo);
        GL.Viewport(0, 0, width, height);
        _renderer.SeekForOfflineRender(time);
        Vector4 background = _renderer.BackgroundColor;
        GL.ClearColor(background.X, background.Y, background.Z, background.W);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        if (_exportZoomOverride || !_renderer.HasCameraEventZoom)
        {
            _renderer.Camera.Zoom = _exportZoom;
            _renderer.Camera.MarkDirty();
        }

        _renderer.Render();
    }

    private float _exportZoom = 0.5f;
    private bool _exportZoomOverride;

    /// <summary>按需创建或重建离屏 FBO / Create or rebuild the offscreen FBO as needed.</summary>
    private void EnsureOffscreenTarget(int width, int height)
    {
        if (_exportFbo != 0 && _exportFboWidth == width && _exportFboHeight == height)
        {
            return;
        }

        ReleaseOffscreenTarget();

        _exportFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _exportFbo);

        _exportColor = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _exportColor);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _exportColor, 0);

        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);

        if (status != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException($"离屏 FBO 不完整 / Offscreen FBO incomplete: {status}");
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        _exportFboWidth = width;
        _exportFboHeight = height;

        Console.Out.WriteLine($"offscreen target {width}x{height}");
    }

    /// <summary>释放离屏 FBO 资源 / Release the offscreen FBO resources.</summary>
    private void ReleaseOffscreenTarget()
    {
        if (_exportFbo != 0)
        {
            GL.DeleteFramebuffer(_exportFbo);
            _exportFbo = 0;
        }

        if (_exportColor != 0)
        {
            GL.DeleteTexture(_exportColor);
            _exportColor = 0;
        }

        _exportFboWidth = 0;
        _exportFboHeight = 0;
    }

    /// <summary>
    /// 面板与窗口共享的导出状态快照：按钮按下后由面板读取进度，窗口负责推进。
    /// Export state shared between the panel and the window: the panel polls progress, the window
    /// drives it.
    /// </summary>
    public readonly record struct VideoExportStatus(
        bool Active,
        int Frame,
        int FrameCount,
        double ElapsedSeconds,
        string? OutputPath,
        string? Error)
    {
        /// <summary>完成比例 0..1。</summary>
        public double Progress => FrameCount > 0 ? Math.Clamp(Frame / (double)FrameCount, 0.0, 1.0) : 0.0;
        public double FramesPerSecond => ElapsedSeconds > 0.0 ? Frame / ElapsedSeconds : 0.0;

        /// <summary>按当前速率估算的剩余秒数；无法估算时为 -1。</summary>
        public double EtaSeconds
        {
            get
            {
                if (Frame <= 0 || ElapsedSeconds <= 0.0 || Frame >= FrameCount)
                {
                    return -1.0;
                }

                double perFrame = ElapsedSeconds / Frame;
                return perFrame * (FrameCount - Frame);
            }
        }
    }

    /// <summary>当前导出状态。面板据此显示进度条。</summary>
    public VideoExportStatus ExportStatus => new(
        ExportRequest is not null || _exportFinalising,
        _exportFrame,
        _exportFrameCount,
        _exportStartedAt > 0.0 ? _clock.Elapsed.TotalSeconds - _exportStartedAt : 0.0,
        _exportOutputPath,
        _lastExportError);

    private bool _exportFinalising;
    private string? _exportOutputPath;
    private string? _lastExportError;
    private (double Time, float Zoom, bool HidePassedFloors) _previewBeforeExport;

    /// <summary>面板请求导出：用给定的分辨率与帧率开始。</summary>
    /// <param name="width">视频宽度。</param>
    /// <param name="height">视频高度。</param>
    /// <param name="fps">帧率。</param>
    /// <param name="zoom">取景缩放。</param>
    /// <returns>成功排入队列时为 true。</returns>
    public bool RequestVideoExport(int width, int height, int fps, float zoom, bool preferHardware = true)
    {
        if (ExportRequest is not null || _exportFinalising)
        {
            return false;
        }

        if (_renderer.IsLoading || _renderer.Level is not { pathToLevel: string levelPath } level
            || string.IsNullOrWhiteSpace(levelPath))
        {
            return false;
        }

        // 输出到关卡旁边，文件名取关卡名；若该名字已被关卡引用的素材占用则自动避开
        // Write next to the level, named after it; if that name already belongs to a level asset,
        // step aside instead of overwriting it
        string output = VideoExporter.ResolveOutputPath(levelPath, level);

        _exportFrame = 0;
        _exportFrameCount = 0;
        _exportStartedAt = 0.0;
        _exportArmed = false;
        _exportFinalising = false;
        _lastExportError = null;
        _exportOutputPath = output;
        _isInteractiveExport = true;
        _previewBeforeExport = (_renderer.PlayTime, _renderer.Camera.Zoom, _renderer.HidePassedFloors);
        _renderer.Pause();
        _panning = _pressPending = false;

        ExportRequest = new VideoExporter.Request(
            levelPath,
            output,
            width,
            height,
            fps,
            KeepFrames: false,
            BitrateKbps: 0,
            Zoom: zoom,
            HidePassedFloors: false,
            PreferHardware: preferHardware);

        return true;
    }

    /// <summary>取消正在进行的导出。</summary>
    public void CancelVideoExport()
    {
        if (ExportRequest is null)
        {
            return;
        }

        ExportRequest = null;
        _exportFinalising = false;

        ReleaseVideoResources();
        _lastExportError = "已取消渲染";
        RestorePreviewAfterVideoRender();
    }

    /// <summary>准备导出：确定尺寸、帧数与序列帧目录。</summary>
    private void ArmExport(VideoExporter.Request request)
    {
        _exportArmed = true;

        // libx264 的 yuv420p 要求偶数宽高，这里直接对齐，避免 ffmpeg 中途退出
        // libx264's yuv420p needs even dimensions, so snap them here before ffmpeg ever sees them
        (int alignedWidth, int alignedHeight) = VideoExporter.AlignEven(request.Width, request.Height);

        _exportWidth = Math.Max(16, alignedWidth);
        _exportHeight = Math.Max(16, alignedHeight);
        _exportFps = Math.Max(1, request.Fps);
        int maxTextureSize = GL.GetInteger(GetPName.MaxTextureSize);
        if (_exportWidth > maxTextureSize || _exportHeight > maxTextureSize
            || (long)_exportWidth * _exportHeight * 4 > int.MaxValue)
        {
            throw new InvalidOperationException($"设备无法渲染 {_exportWidth}×{_exportHeight} 的视频");
        }

        // 帧数按关卡总时长向上取整，保证最后一拍也被画到
        // Frame count rounds the level duration up so the final beat is drawn too
        _exportFrameCount = (int)Math.Ceiling(_renderer.ExportDuration / _renderer.PlaybackRate * _exportFps);

        if (_exportFrameCount <= 0)
        {
            _exportFrameCount = 1;
        }

        string frameDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(request.OutputPath)) ?? ".",
            Path.GetFileNameWithoutExtension(request.OutputPath) + "_frames");

        // 相机视口跟着导出分辨率走，这样取景比例与导出画面一致
        // The camera viewport follows the export resolution so framing matches the exported picture
        _renderer.Resize(_exportWidth, _exportHeight);
        _renderer.Camera.MarkDirty();

        _exportSettleFrames = 3;
        _exportStartedAt = _clock.Elapsed.TotalSeconds;
        _exportDrawTicks = _exportReadbackTicks = _exportQueueTicks = 0;
        string? ffmpeg = VideoExporter.FindFfmpeg();
        VideoExporter.EncoderSession? encoder = null;
        if (ffmpeg is null)
        {
            Console.Error.WriteLine("ffmpeg 未找到，回退为 PNG 序列输出 / ffmpeg not found; falling back to PNG frames");
            _exportOutputPath = frameDirectory;
        }
        else
        {
            bool canPack = !request.KeepFrames && _exportWidth % 4 == 0
                && _exportHeight + _exportHeight / 2 <= maxTextureSize;
            if (canPack)
            {
                try { _videoColorConverter = new VideoColorConverter(_exportWidth, _exportHeight); }
                catch (InvalidOperationException ex) { Console.Error.WriteLine($"GPU NV12 unavailable: {ex.Message}"); }
            }
            encoder = VideoExporter.StartEncoder(request, _exportWidth, _exportHeight, WriteExportAudio(), ffmpeg,
                _videoColorConverter is not null, Console.Out.WriteLine);
        }
        try
        {
            _videoReadback = new VideoReadback(_videoColorConverter?.Width ?? _exportWidth,
                _videoColorConverter?.Height ?? _exportHeight, encoder is { PackedNv12: true });
            _videoWriter = new VideoFrameWriter(encoder, encoder is null || request.KeepFrames ? frameDirectory : null,
                _exportWidth, _exportHeight);
        }
        catch
        {
            encoder?.Cancel();
            encoder?.Dispose();
            throw;
        }

        // 导出时让经过的砖块消失：录像里只保留当前砖块与行星，画面干净
        // Hide passed tiles while exporting: the recording then shows only the current tile and the
        // planet, which reads much cleaner
        _renderer.HidePassedFloors = request.HidePassedFloors;

        _exportZoomOverride = request.Zoom > 0f;
        _exportZoom = _exportZoomOverride ? request.Zoom : _renderer.GetExportZoom();

        _renderer.Camera.Zoom = _exportZoom;
        _renderer.Camera.MarkDirty();

        Console.Out.WriteLine(
            $"export start {_exportWidth}x{_exportHeight} @{_exportFps}fps, {_exportFrameCount} frames");
        Console.Out.WriteLine($"export GPU: {GL.GetString(StringName.Renderer)}");
    }

    /// <summary>导出结束：混音、编码、清理。</summary>
    private void FinishExport(VideoExporter.Request request, Exception? failure = null)
    {
        _exportFinalising = true;
        ExportRequest = null;

        try
        {
            if (failure is not null)
                ReportExportFailure(failure);
            else if (_videoWriter is { } writer && _videoReadback is { } readback)
            {
                while (readback.Pending > 0 && !writer.Failed)
                    QueueVideoFrame(readback, writer);
                writer.Finish();

                double elapsed = _clock.Elapsed.TotalSeconds - _exportStartedAt;
                double msPerTick = 1000.0 / (Stopwatch.Frequency * (double)_exportFrame);
                Console.Out.WriteLine(FormattableString.Invariant(
                    $"exported {_exportFrame} frames in {elapsed:F2}s ({_exportFrame / elapsed:F1} frames/s) · draw {_exportDrawTicks * msPerTick:F2} ms · readback {_exportReadbackTicks * msPerTick:F2} ms · encoder queue {_exportQueueTicks * msPerTick:F2} ms per frame"));
                Console.Out.WriteLine($"video -> {_exportOutputPath ?? request.OutputPath}");
                _lastExportError = null;
            }
        }
        catch (Exception ex)
        {
            ReportExportFailure(ex);
        }

        ReleaseVideoResources();
        _exportFinalising = false;
        if (_isInteractiveExport)
            RestorePreviewAfterVideoRender();
        else
        {
            if (_lastExportError is not null)
                Environment.ExitCode = 1;
            Close();
        }
    }

    private void ReportExportFailure(Exception error)
    {
        Console.Error.WriteLine($"export failed: {error}");
        _lastExportError = error.Message;
    }

    private void ReleaseVideoResources()
    {
        _videoWriter?.Dispose();
        _videoWriter = null;
        _videoReadback?.Dispose();
        _videoReadback = null;
        _videoColorConverter?.Dispose();
        _videoColorConverter = null;
        if (_encoderAudioPath is { } path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _encoderAudioPath = null;
        }
    }

    private void RestorePreviewAfterVideoRender()
    {
        if (!_exportArmed)
            return;

        _renderer.Resize(ClientSize.X, ClientSize.Y);
        _renderer.Camera.Zoom = _previewBeforeExport.Zoom;
        _renderer.Camera.MarkDirty();
        _renderer.HidePassedFloors = _previewBeforeExport.HidePassedFloors;
        _renderer.RestorePreviewAfterOfflineRender(_previewBeforeExport.Time);
    }

    /// <summary>本次导出是否由面板发起（而不是命令行）。</summary>
    private bool _isInteractiveExport;

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

        if (_fullscreenTogglePending)
        {
            _fullscreenTogglePending = false;
            ToggleFullscreen();
        }

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
        if (ExportRequest is not null)
        {
            RenderExportFrame();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        else
            UpdatePan();

        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        Vector4 background = _renderer.BackgroundColor;
        GL.ClearColor(background.X, background.Y, background.Z, background.W);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (ExportRequest is null)
        {
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            _renderer.Render();
        }

        DrawOverlay((float)e.Time);
    }

    private void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            WindowState = _windowedState;
        }
        else
        {
            _windowedState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowState = WindowState.Fullscreen;
        }
    }

    /// <summary>绘制菜单与面板并提交 ImGui。</summary>
    private void DrawOverlay(float lastDeltaTime)
    {
        ImGuiIO* io = ImGui.GetIO();
        io->DeltaTime = lastDeltaTime;
        io->DisplaySize = new ImVec2 { x = ClientSize.X, y = ClientSize.Y };
        io->DisplayFramebufferScale = new ImVec2 { x = 1f, y = 1f };

        ImGui_ImplOpenGL3.NewFrame();
        ImGui.NewFrame();

        bool rendering = ExportRequest is not null;
        float menuBottom = DrawMenuBar(rendering);
        _levelPanel.Draw(_renderer, menuBottom, rendering, _audioCalibration);
        _videoRenderWindow.Draw(this, _renderer.Level is not null && !_renderer.IsLoading);
        _audioCalibration.Draw(_renderer);

        if (_showDemo)
        {
            bool open = true;
            ImGui.ShowDemoWindow(ref open);
            _showDemo = open;
        }

        if (ExportRequest is null && !_audioCalibration.IsOpen)
            ManualPlayHud.Draw(_renderer);

        ImGui.Render();
        ImDrawData* drawData = ImGui.GetDrawData();
        ImGui_ImplOpenGL3.RenderDrawData(drawData);

        UpdateCapture(drawData);

        SwapBuffers();
    }

    /// <summary>
    /// Drive the command-line capture: wait for the load to finish, give the renderer a few frames to
    /// settle, apply the requested zoom, then write the PNG and close the window.
    /// </summary>
    private void UpdateCapture(ImDrawData* drawData)
    {
        if (CapturePath is null || _captureDone)
        {
            return;
        }

        if (_renderer.LoadError is { } error)
        {
            Console.Error.WriteLine($"capture failed: {error}");
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
                // Seek first: the selected floor and camera framing are resolved from the
                // playback time, so the order matters. Pause so the captured frame is stable.
                _renderer.Seek(CaptureTime);
                if (_renderer.IsPlaying)
                    _renderer.Pause();
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
            foreach ((string phase, double milliseconds) in _renderer.LoadTimings)
            {
                Console.Out.WriteLine(FormattableString.Invariant($"load {phase} {milliseconds:F1}ms"));
            }
            int uiCommands = CountOverlayDrawCommands(drawData);
            Console.Out.WriteLine(
                $"floors={_renderer.FloorCount} floorBatches={_renderer.FloorBatchCount} " +
                $"floorDrawCalls={_renderer.FloorDrawCalls} sceneDrawCalls={_renderer.DrawCalls} " +
                $"uiDrawCommands={uiCommands}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture failed: {ex.Message}");
        }

        Close();
    }

    private static int CountOverlayDrawCommands(ImDrawData* drawData)
    {
        int count = 0;
        for (int i = 0; i < drawData->CmdListsCount; i++)
        {
            ImDrawList* list = (ImDrawList*)drawData->CmdLists.Data[i];
            for (int j = 0; j < list->CmdBuffer.Size; j++)
            {
                ImDrawCmd* command = &list->CmdBuffer.Data[j];
                if (command->ElemCount != 0 && command->UserCallback == null)
                {
                    count++;
                }
            }
        }

        return count;
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
            if (ImGui.MenuItem("全屏", "Alt+Enter", (byte)(IsFullscreen ? 1 : 0)) != 0)
                _fullscreenTogglePending = !_fullscreenTogglePending;

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

        string status = rendering ? "视频渲染中…" : _renderer.Status;
        float width = ImGui.CalcTextSize(status, null, 0, -1f).x;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - width - 16f));
        ImGui.TextUnformatted(status);

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

    private static void LoadFont()
    {
        ImGuiIO* io = ImGui.GetIO();
        ImFontAtlas* atlas = io->Fonts;
        ushort* ranges = ImFontAtlas.GetGlyphRangesChineseSimplifiedCommon(atlas);

        ImFont* font = null;
        foreach (string path in FontCandidates())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            font = ImFontAtlas.AddFontFromFileTTF(atlas, path, 16f, null, ranges);
            if (font != null)
            {
                break;
            }
        }

        if (font == null)
        {
            Console.Error.WriteLine("[字体] 未找到中文字体，界面中文将无法显示");
        }

        io->FontDefault = font != null ? font : ImFontAtlas.AddFontDefault(atlas);
    }

    /// <summary>
    /// Chinese font candidates per platform: Windows system fonts, common Linux/macOS locations,
    /// and finally a fontconfig lookup for the default CJK sans-serif.
    /// </summary>
    private static IEnumerable<string> FontCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Windows\Fonts\msyh.ttc";
            yield return @"C:\Windows\Fonts\Deng.ttf";
            yield return @"C:\Windows\Fonts\simhei.ttf";
            yield break;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] paths =
        [
            // Fonts the user installed (WSL commonly shares Windows fonts here)
            Path.Combine(home, ".local/share/fonts/msyh.ttc"),
            Path.Combine(home, ".local/share/fonts/Deng.ttf"),
            Path.Combine(home, ".local/share/fonts/simhei.ttf"),
            Path.Combine(home, ".fonts/msyh.ttc"),
            // Shipped by the distribution
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/opentype/noto/NotoSansCJKsc-Regular.otf",
            "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-microhei.ttc",
            "/usr/share/fonts/truetype/wqy/wqy-zenhei.ttc",
            "/usr/share/fonts/truetype/arphic/uming.ttc",
            "/usr/share/fonts/truetype/droid/DroidSansFallbackFull.ttf",
            // macOS
            "/System/Library/Fonts/PingFang.ttc",
            "/System/Library/Fonts/STHeiti Light.ttc",
        ];

        foreach (string path in paths)
        {
            yield return path;
        }

        string? system = FindSystemCjkFont();
        if (system != null)
        {
            yield return system;
        }
    }

    /// <summary>Ask fontconfig for the default CJK font.</summary>
    private static string? FindSystemCjkFont()
    {
        try
        {
            ProcessStartInfo info = new("fc-match", ["-f", "%{file}", "sans-serif:lang=zh-cn"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(info);
            if (process == null)
            {
                return null;
            }

            string path = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return path.Length > 0 && File.Exists(path) ? path : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[字体] fontconfig 查询失败: {ex.Message}");
            return null;
        }
    }
}
