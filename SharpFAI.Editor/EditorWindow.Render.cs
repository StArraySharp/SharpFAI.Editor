using OpenTK.Graphics.OpenGL4;
using SharpFAI.ModApi;
using SharpFAI.Player;
using SharpFAI.Player.Engine;
using Thrust.Graphics;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly VideoRenderWindow _videoRenderWindow = new(L.T);
    private LevelRenderer? _videoRenderSource;
    private VideoExportSession? _videoExport;
    private bool _cancelVideoExport;
    private PlaybackRange? _videoRenderRange;
    private EditorDocument? _videoRangeDocument;

    private void OpenVideoRender(PlaybackRange? range = null)
    {
        if (_videoExport?.Request is null)
        {
            _videoRenderRange = range;
            _videoRangeDocument = range.HasValue ? _document : null;
        }
        _videoRenderWindow.Open();
    }

    private void DrawVideoRender()
    {
        string? unavailable = _videoRangeDocument is not null && _videoRangeDocument != _document
            ? L.T("VideoRenderRangeChanged") : _document.Path is null ? L.T("VideoRenderSaveFirst") : null;
        _videoRenderWindow.Draw(_videoExport?.Status ?? default, StartVideoRender, CancelVideoRender,
            unavailable, _videoRenderRange);
    }

    private void StartVideoRender(VideoRenderWindow.Settings settings)
    {
        if (_videoExport?.Request is not null || _modPerformanceJob is not null || _document.Path is not { } sourcePath)
            return;
        DisposeModFrameCapture();

        try
        {
            FinishNumericEdit();
            var snapshot = _document.SnapshotForPlayback();
            var request = settings.CreateRequest(sourcePath, snapshot, _videoRenderRange);
            StartVideoJob(request, snapshot);
        }
        catch (Exception error)
        {
            _videoRenderSource?.Unload();
            ShowError(error);
        }
    }

    private void StartVideoJob(VideoExporter.Request request, SharpFAI.Serialization.Level snapshot)
    {
        LevelRenderer renderer = _videoRenderSource ??= new LevelRenderer
            { EnableAudioOutput = false, Mods = _modUi?.Host, ModTarget = UiHost.Export };
        renderer.SkipAudio = _applicationSettings.SkipAudio;
        renderer.ApplyVisualSettings(_applicationSettings);
        _videoExport ??= new VideoExportSession(renderer);
        if (!renderer.Load(snapshot, request.LevelPath))
            throw new InvalidOperationException(renderer.LoadError ?? renderer.Status);
        _videoExport.Start(request, interactive: true);
    }

    private void CancelVideoRender() => _cancelVideoExport = true;

    private void UpdateVideoRender()
    {
        if (_videoExport is not { Request: not null } export || _videoRenderSource is not { } renderer)
            return;

        int readFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        using var gl = new GlRenderStateScope();
        try
        {
            if (_cancelVideoExport)
            {
                _cancelVideoExport = false;
                export.Cancel();
            }
            else
            {
                renderer.Tick(_playbackClock.Elapsed.TotalSeconds);
                export.Advance();
            }
        }
        catch (Exception error)
        {
            export.Cancel();
            if (_modVideoJob is { State: "running" } job)
                _modVideoJob = job with { State = "failed", Error = error.Message };
            ShowError(error);
        }
        finally
        {
            TrackModVideoJob(export.Status);
            // Unload the chart without shutting down OpenAL, which the main playback scene still uses.
            if (export.Request is null)
                renderer.Unload();
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
        }
    }

    private void DisposeVideoRender()
    {
        _videoExport?.Dispose();
        _videoRenderSource?.Dispose();
    }
}
