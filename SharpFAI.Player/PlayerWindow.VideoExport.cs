using SharpFAI.Player.Engine;

namespace SharpFAI.Player;

internal sealed unsafe partial class PlayerWindow
{
    private readonly VideoExportSession _videoExport;
    private bool _isInteractiveExport;
    private (double Time, float Zoom, bool HidePassedFloors) _previewBeforeExport;

    public VideoExporter.Request? ExportRequest
    {
        get => _videoExport.Request;
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
        _videoExport.Start(request, interactive: true);
    }

    private void CancelVideoExport()
    {
        if (ExportRequest is null)
            return;

        _videoExport.Cancel();
        RestorePreviewAfterVideoRender();
    }

    private void RenderExportFrame()
    {
        _videoExport.Advance();
        if (ExportRequest is not null)
            return;

        if (_isInteractiveExport)
            RestorePreviewAfterVideoRender();
        else
        {
            if (_videoExport.Status.Error is not null)
                Environment.ExitCode = 1;
            Close();
        }
    }

    private void RestorePreviewAfterVideoRender()
    {
        if (!_videoExport.Armed)
            return;

        _renderer.Resize(ClientSize.X, ClientSize.Y);
        _renderer.Camera.Zoom = _previewBeforeExport.Zoom;
        _renderer.Camera.MarkDirty();
        _renderer.HidePassedFloors = _previewBeforeExport.HidePassedFloors;
        _renderer.RestorePreviewAfterOfflineRender(_previewBeforeExport.Time);
    }
}
