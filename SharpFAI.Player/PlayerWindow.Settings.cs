using ImGui4NET;

namespace SharpFAI.Player;

internal sealed unsafe partial class PlayerWindow
{
    private readonly ApplicationSettingsStore _settingsStore = ApplicationSettingsStore.ForApplication(editor: false);
    private readonly ApplicationSettingsWindow _settingsWindow = new();
    private readonly ApplicationWindowModeController _windowMode = new();
    private readonly DisplayFrameLimit _displayFrameLimit = new();
    private ApplicationSettings _applicationSettings = ApplicationSettings.PlayerDefaults;
    private ApplicationSettings? _pendingApplicationSettings;

    private void LoadApplicationSettings()
    {
        if (ExportRequest is not null || CapturePath is not null)
        {
            _renderer.EnableAudioOutput = false;
            _renderer.SkipAudio = SkipAudioFromCommandLine;
            _renderer.ApplyVisualSettings(WithCommandLineColors(ApplicationSettings.PlayerDefaults));
            return;
        }

        _applicationSettings = _settingsStore.Load();
        ApplyApplicationSettings();
    }

    private void ApplyApplicationSettings()
    {
        _windowMode.Apply(this, _applicationSettings.WindowMode);
        _applicationSettings.ApplyFramePacing(this);
        _renderer.SkipAudio = SkipAudioFromCommandLine || _applicationSettings.SkipAudio;
        _renderer.ApplyVisualSettings(WithCommandLineColors(_applicationSettings));
        _renderer.SetPlaybackOutputVolumes(_applicationSettings.MasterVolume, _applicationSettings.MusicVolume,
            _applicationSettings.HitsoundVolume);
        _audioCalibration.SetOutputVolume(_applicationSettings.MasterVolume);
    }

    private void ApplyPendingApplicationSettings()
    {
        bool settingsChanged = _pendingApplicationSettings is not null;
        if (_pendingApplicationSettings is { } settings)
        {
            _pendingApplicationSettings = null;
            _applicationSettings = settings;
            ApplyApplicationSettings();
        }
        var mode = _windowMode.ApplyPending(this);
        if (mode is { } current)
        {
            _applicationSettings = _applicationSettings with { WindowMode = current };
            if (ExportRequest is null && CapturePath is null)
                _settingsStore.TrySave(_applicationSettings);
        }
        if (ExportRequest is null && CapturePath is null)
            _displayFrameLimit.Update(this, _applicationSettings, settingsChanged || mode.HasValue);
    }

    private ApplicationSettings WithCommandLineColors(ApplicationSettings settings) => settings with
    {
        FirePlanetColor = FireColorFromCommandLine ?? settings.FirePlanetColor,
        IcePlanetColor = IceColorFromCommandLine ?? settings.IcePlanetColor,
    };

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

        _pendingApplicationSettings = settings;
        return true;
    }
}
