using SharpFAI.ModApi;
using SharpFAI.Player;
using ModRuntime = SharpFAI.Editor.Mods.ModHost;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private ModUi? _modUi;
    internal bool DisableMods { get; init; }

    private void InitializeMods()
    {
        var host = new ModRuntime(UiHost.Editor, safeMode: DisableMods) { Culture = () => L.Language };
        _modUi = new(host, () => ModContext(false), fullscreen =>
        {
            if (_windowMode.IsFullscreen != fullscreen) _windowMode.RequestToggle();
        }, ModTransport, CommitModEdit, () => ModContext(true));
        var inspection = new ModEditorInspection(this, _modUi);
        var resolveUiService = host.ExternalService;
        host.ExternalService = type =>
        {
            if (type == typeof(IEditorInspection) || type == typeof(IEditorFrameCapture) ||
                type == typeof(IEditorWorkflow)) return inspection;
            if (type == typeof(IEditorWindowStream)) return _windowStream;
            return resolveUiService?.Invoke(type);
        };
        _modUi.Initialize();
    }

    private EditResult CommitModEdit(ChartRevision revision, ChartEditRequest edit)
    {
        if (_playbackView != PlaybackView.Edit || _videoExport?.Request is not null) return EditResult.Invalid;
        FinishNumericEdit();
        EditResult result = _document.ApplyModEdits(revision, edit);
        if (result == EditResult.Applied) ClampSelection();
        return result;
    }

    private CommandContext ModContext(bool snapshot)
    {
        PlaybackMode mode = _playbackView switch
        {
            PlaybackView.Preview => PlaybackMode.Preview,
            PlaybackView.Auto => PlaybackMode.Auto,
            PlaybackView.Manual => PlaybackMode.Manual,
            _ => PlaybackMode.Edit,
        };
        return new(_document.ModView(_floor, snapshot), new(mode, _playback?.PlayTime ?? 0,
            _playback?.Duration ?? 0, _playback?.CurrentIndex ?? _floor, _playback?.IsPlaying == true));
    }

    private void TickMods() => _modUi?.Tick(new(new(ClientSize.X, ClientSize.Y),
        new(FramebufferSize.X, FramebufferSize.Y), ClientSize.X > 0 ? (float)FramebufferSize.X / ClientSize.X : 1,
        IsFocused, _windowMode.IsFullscreen), _canvasOnly);

    private void DrawModSettings() =>
        _modUi?.DrawSettings(!_receivedRemoteInput && _remoteButtons == 0 && _remoteKeys.Count == 0);

    private bool ModTransport(TransportAction action, double time)
    {
        if (_videoExport?.Request is not null || _settingsWindow.IsOpen ||
            _playbackView == PlaybackView.Manual && action != TransportAction.Edit)
            return false;
        switch (action)
        {
            case TransportAction.Play:
                if (_playbackView == PlaybackView.Auto) _playback?.Play();
                else SwitchPlaybackView(PlaybackView.Auto);
                return true;
            case TransportAction.Pause when _playbackView != PlaybackView.Edit:
                PauseToPreview();
                return true;
            case TransportAction.Seek when double.IsFinite(time) && HasCurrentPlayback && _playback is not null:
                if (_playbackView == PlaybackView.Edit) SwitchPlaybackView(PlaybackView.Preview);
                else PauseToPreview();
                _playback.Seek(Math.Clamp(time, 0, _playback.Duration));
                return true;
            case TransportAction.Edit:
                ReturnToEdit();
                return true;
            default: return false;
        }
    }
}
