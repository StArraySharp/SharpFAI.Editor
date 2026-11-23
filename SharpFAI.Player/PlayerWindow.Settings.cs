using ImGui4NET;

namespace SharpFAI.Player;

internal sealed unsafe partial class PlayerWindow
{
    private readonly ApplicationSettingsStore _settingsStore = ApplicationSettingsStore.ForApplication(editor: false);
    private readonly ApplicationSettingsWindow _settingsWindow = new();
    private ApplicationSettings _applicationSettings = ApplicationSettings.PlayerDefaults;

    private void LoadApplicationSettings()
    {
        if (ExportRequest is not null || CapturePath is not null)
        {
            _renderer.SkipAudio = SkipAudioFromCommandLine;
            return;
        }

        _applicationSettings = _settingsStore.Load();
        ApplyApplicationSettings();
    }

    private void ApplyApplicationSettings()
    {
        _applicationSettings.ApplyFramePacing(this);
        _renderer.SkipAudio = SkipAudioFromCommandLine || _applicationSettings.SkipAudio;
        _renderer.SetPlaybackOutputVolumes(_applicationSettings.MasterVolume, _applicationSettings.MusicVolume,
            _applicationSettings.HitsoundVolume);
        _audioCalibration.SetOutputVolume(_applicationSettings.MasterVolume);
    }

    private void DrawSettingsMenuItem(bool rendering)
    {
        if (ImGui.MenuItem("设置", null, 0, (byte)(rendering ? 0 : 1)) == 0)
            return;

        if (_renderer.ManualPlayEnabled)
        {
            _renderer.Pause();
            _renderer.PlayInput.ReleaseAll(_clock.Elapsed.TotalSeconds);
        }
        _panning = _pressPending = false;
        _settingsWindow.Open(_applicationSettings);
    }

    private bool TryApplyApplicationSettings(ApplicationSettings settings)
    {
        if (!_settingsStore.TrySave(settings))
            return false;

        _applicationSettings = settings;
        ApplyApplicationSettings();
        return true;
    }
}
