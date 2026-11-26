using SharpFAI.Player.Engine;
using SharpFAI.ModApi;

namespace SharpFAI.Player;

internal sealed unsafe partial class PlayerWindow
{
    private readonly VideoExportSession _videoExport;
    private LevelRenderer? _modExportRenderer;
    private VideoExportSession? _modExportSession;
    private VideoExportSession CurrentExport => _modExportSession ?? _videoExport;
    private bool _isInteractiveExport;
    private (double Time, float Zoom, bool HidePassedFloors) _previewBeforeExport;

    public VideoExporter.Request? ExportRequest
    {
        get => CurrentExport.Request;
        set
        {
            if (value is { } request)
                _videoExport.Start(request, interactive: false);
        }
    }

    private void RequestVideoExport(VideoRenderWindow.Settings settings)
    {
        if (ExportRequest is not null || _renderer.IsLoading ||
            _renderer.Level is not { pathToLevel: { Length: > 0 } levelPath } level)
            return;

        var request = settings.CreateRequest(levelPath, level);
        _isInteractiveExport = true;
        _previewBeforeExport = (_renderer.PlayTime, _renderer.Camera.Zoom, _renderer.HidePassedFloors);
        _renderer.Pause();
        _panning = _pressPending = false;
        if (_renderer.Mods is { } mods && (mods.HasLoadHooks(UiHost.Player) || mods.HasLoadHooks(UiHost.Export)))
        {
            _modExportRenderer = new LevelRenderer { Mods = mods, ModTarget = UiHost.Export, EnableAudioOutput = false,
                SkipAudio = _renderer.SkipAudio };
            _modExportRenderer.ApplyVisualSettings(WithCommandLineColors(_applicationSettings));
            if (!_modExportRenderer.Load(levelPath))
            {
                mods.Report("SharpFAI", _modExportRenderer.LoadError ?? _modExportRenderer.Status);
                ClearModExport();
                return;
            }
            _modExportSession = new VideoExportSession(_modExportRenderer);
        }
        CurrentExport.Start(request, interactive: true);
    }

    private void CancelVideoExport()
    {
        if (ExportRequest is null)
            return;

        CurrentExport.Cancel();
        if (ExportRequest is null) RestorePreviewAfterVideoRender();
    }

    private void RenderExportFrame()
    {
        _modExportRenderer?.Tick(_clock.Elapsed.TotalSeconds);
        CurrentExport.Advance();
        if (ExportRequest is not null)
            return;

        if (_isInteractiveExport)
            RestorePreviewAfterVideoRender();
        else
        {
            if (_videoExport.Status.Error is not null)
                CommandExitCode = 1;
            Close();
        }
    }

    private void RestorePreviewAfterVideoRender()
    {
        if (_modExportRenderer is not null)
        {
            ClearModExport();
            return;
        }
        if (!_videoExport.Armed)
            return;

        _renderer.Resize(ClientSize.X, ClientSize.Y);
        _renderer.Camera.Zoom = _previewBeforeExport.Zoom;
        _renderer.Camera.MarkDirty();
        _renderer.HidePassedFloors = _previewBeforeExport.HidePassedFloors;
        _renderer.RestorePreviewAfterOfflineRender(_previewBeforeExport.Time);
    }

    private void ClearModExport()
    {
        _modExportSession?.Dispose();
        _modExportRenderer?.Dispose();
        _modExportSession = null;
        _modExportRenderer = null;
    }
}
