using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine;
using System.Diagnostics;

namespace SharpFAI.Player;

/// <summary>Runs one export at a time on the caller's GL thread. The caller owns and ticks the renderer.</summary>
internal sealed class VideoExportSession(LevelRenderer renderer) : IDisposable
{
    private readonly LevelRenderer _renderer = renderer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public VideoExporter.Request? Request { get; private set; }
    public bool Armed => _exportArmed;

    public void Advance()
    {
        if (Request is not { } request)
            return;

        try { RenderExportSteps(FramesPerCallback); }
        catch (Exception error) when (Request is not null) { FinishExport(request, error); }
    }

    public void Dispose() => Cancel();

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

    /// <summary>
    /// 推进至多 <paramref name="budget"/> 帧。渲染与编码都在这里完成，调用方负责绘制 UI。
    /// Advance up to `budget` frames; rendering and encoding happen here and the caller draws the UI.
    /// </summary>
    private void RenderExportSteps(int budget)
    {
        VideoExporter.Request request = Request!.Value;

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

            if (_interactive && Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds >= 12.0)
                break;
        }

        if (writer.Failed)
            FinishExport(request);
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
                _renderer.PlaybackRate,
                hitsoundRate: 1.0);

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
        GL.Disable(EnableCap.ScissorTest);
        GL.ColorMask(true, true, true, true);
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
        string? Error,
        bool Cancelled)
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

    private string? _exportOutputPath;
    private string? _lastExportError;
    private bool _cancelled;
    private bool _interactive;

    public VideoExportStatus Status => new(
        Request is not null,
        _exportFrame,
        _exportFrameCount,
        _exportStartedAt > 0.0 ? _clock.Elapsed.TotalSeconds - _exportStartedAt : 0.0,
        _exportOutputPath,
        _lastExportError,
        _cancelled);

    public void Start(VideoExporter.Request request, bool interactive)
    {
        if (Request is not null)
            throw new InvalidOperationException("A video export is already running.");

        _exportFrame = 0;
        _exportFrameCount = 0;
        _exportStartedAt = 0.0;
        _exportArmed = false;
        _exportWaitingLogged = false;
        _lastExportError = null;
        _cancelled = false;
        _exportOutputPath = request.OutputPath;
        _interactive = interactive;
        Request = request;
    }

    public void Cancel()
    {
        if (Request is null)
            return;

        Request = null;
        _cancelled = true;
        _exportOutputPath = null;
        ReleaseVideoResources();
        ReleaseOffscreenTarget();
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
        Request = null;

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
        ReleaseOffscreenTarget();
    }

    private void ReportExportFailure(Exception error)
    {
        Console.Error.WriteLine($"export failed: {error}");
        _lastExportError = error.Message;
        _exportOutputPath = null;
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
}
