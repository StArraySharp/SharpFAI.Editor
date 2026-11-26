using OpenTK.Graphics.OpenGL4;
using SharpFAI.ModApi;
using SharpFAI.Player;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private sealed class ModFrameJob(FrameCaptureRequest request, Level chart, string sourcePath, CancellationToken token)
        : IDisposable
    {
        public FrameCaptureRequest Request { get; } = request;
        public Level Chart { get; } = chart;
        public string SourcePath { get; } = sourcePath;
        public CancellationToken Token { get; } = token;
        public LevelRenderer? Renderer { get; set; }
        public int Framebuffer { get; set; }
        public int Color { get; set; }
        public int FramesRemaining { get; set; }
        public double TargetTime { get; set; }
        public TaskCompletionSource<(ChartFrameState State, byte[]? Pixels)> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            Renderer?.Dispose();
            if (Framebuffer != 0) GL.DeleteFramebuffer(Framebuffer);
            if (Color != 0) GL.DeleteTexture(Color);
            Completion.TrySetCanceled();
        }
    }

    private ModFrameJob? _modFrameJob;

    private ModFrameJob StartModFrameCapture(FrameCaptureRequest request, EditorDocument document, CancellationToken token)
    {
        if (_modFrameJob is not null || _videoExport?.Request is not null)
            throw new InvalidOperationException("A render is already running. Retry when it completes.");
        if (request.Width is < 16 or > 4096 || request.Height is < 16 or > 4096 ||
            (long)request.Width * request.Height > 4096L * 2160 ||
            request.PrerollFps is < 1 or > 120 || !double.IsFinite(request.PrerollSeconds) ||
            request.PrerollSeconds is < 0 or > 10)
            throw new ArgumentException("Invalid frame dimensions or preroll. Use up to 4096 × 2160 pixels and 10 seconds.");
        if (request.Floor.HasValue == request.Seconds.HasValue ||
            request.Floor is int floor && (floor < 0 || floor > document.LastFloor) ||
            request.Seconds is double seconds && (!double.IsFinite(seconds) || seconds < 0))
            throw new ArgumentException("Provide an existing floor or a finite, nonnegative time in seconds.");
        return _modFrameJob = new(request, document.SnapshotForPlayback(), document.PlaybackPath, token);
    }

    private void UpdateModFrameCapture()
    {
        if (_modFrameJob is not { } job || _videoExport?.Request is not null) return;
        int readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int packBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        using var state = new GlRenderStateScope();
        try
        {
            job.Token.ThrowIfCancellationRequested();
            var request = job.Request;
            if (job.Renderer is null)
            {
                var renderer = job.Renderer = new LevelRenderer
                    { EnableAudioOutput = false, SkipAudio = true, Mods = _modUi?.Host, ModTarget = UiHost.Editor };
                renderer.ApplyVisualSettings(_applicationSettings);
                renderer.Resize(request.Width, request.Height);
                renderer.Load(job.Chart, job.SourcePath);
            }
            var source = job.Renderer;
            source.TickInspectionLoad();
            if (source.IsLoading) return;
            if (source.Level is null)
                throw new InvalidOperationException(source.LoadError ?? source.Status);
            if (job.Framebuffer == 0)
                InitializeModCaptureTarget(job, source);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, job.Framebuffer);
            GL.Viewport(0, 0, request.Width, request.Height);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.ColorMask(true, true, true, true);
            double time = job.TargetTime - job.FramesRemaining / (double)request.PrerollFps;
            source.SeekForOfflineRender(time);
            var background = source.BackgroundColor;
            GL.ClearColor(background.X, background.Y, background.Z, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            source.RenderAfterClear(background);
            if (job.FramesRemaining > 0)
            {
                job.FramesRemaining--;
                return;
            }

            byte[]? pixels = null;
            if (request.IncludeImage)
            {
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, job.Framebuffer);
                GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
                pixels = FrameCapture.ReadPixels(request.Width, request.Height);
            }
            job.Completion.TrySetResult((source.InspectRenderedFrame(), pixels));
            DisposeModFrameCapture();
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            DisposeModFrameCapture();
        }
        catch (Exception error)
        {
            job.Completion.TrySetException(error);
            DisposeModFrameCapture();
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, packBuffer);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
        }
    }

    private static void InitializeModCaptureTarget(ModFrameJob job, LevelRenderer source)
    {
        var request = job.Request;
        double seconds = request.Seconds ?? (source.EditorTiming is { } timing &&
            request.Floor is int floor && floor < timing.EntryTimes.Length ? timing.EntryTimes[floor] :
            throw new InvalidOperationException("The prepared chart has no timing for this floor."));
        job.TargetTime = Math.Max(0, seconds);
        if (request.Seconds.HasValue && job.TargetTime > source.Duration)
            throw new ArgumentOutOfRangeException(nameof(request), $"Time exceeds the chart duration ({source.Duration:F3} seconds).");
        job.FramesRemaining = (int)Math.Floor(Math.Min(job.TargetTime, request.PrerollSeconds) * request.PrerollFps);

        job.Framebuffer = GL.GenFramebuffer();
        job.Color = GL.GenTexture();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, job.Framebuffer);
        GL.BindTexture(TextureTarget.Texture2D, job.Color);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, request.Width, request.Height,
            0, PixelFormat.Rgb, PixelType.UnsignedByte, 0);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, job.Color, 0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("Could not create an offscreen framebuffer.");
    }

    private void DisposeModFrameCapture()
    {
        var job = _modFrameJob;
        _modFrameJob = null;
        job?.Dispose();
    }
}
