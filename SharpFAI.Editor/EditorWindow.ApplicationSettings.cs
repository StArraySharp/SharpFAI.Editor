using System.Globalization;
using ImGui4NET;
using SharpFAI.Player;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly ApplicationSettingsStore _settingsStore = ApplicationSettingsStore.ForApplication(editor: true);
    private readonly ApplicationSettingsWindow _settingsWindow = new();
    private ApplicationSettings _applicationSettings = ApplicationSettings.EditorDefaults;
    private ApplicationSettings? _pendingApplicationSettings;
    private readonly string? _commandLineLanguage;

    private void LoadApplicationSettings()
    {
        if (_smokeTest || _effectBenchmark)
            return;

        _applicationSettings = _settingsStore.Load();
        if (_commandLineLanguage is not null)
            _applicationSettings = _applicationSettings with { Language = L.IsChinese ? "zh-Hans" : "en" };
        ApplyApplicationSettings();
    }

    private void ApplyApplicationSettings()
    {
        _applicationSettings.ApplyFramePacing(this);
        L.Language = _applicationSettings.Language is { } language
            ? CultureInfo.GetCultureInfo(language)
            : CultureInfo.CurrentUICulture;
        if (_playback is { } renderer)
            ApplyPreviewAudioSettings(renderer);
    }

    private void ApplyPreviewAudioSettings(Player.Engine.LevelRenderer renderer)
    {
        renderer.SkipAudio = _applicationSettings.SkipAudio;
        renderer.SetPlaybackOutputVolumes(_applicationSettings.MasterVolume, _applicationSettings.MusicVolume,
            _applicationSettings.HitsoundVolume);
    }

    private void DrawApplicationSettingsMenuItem()
    {
        bool enabled = _videoExport?.Request is null;
        if (ImGui.MenuItem(L.IsChinese ? "设置" : "Settings", null, 0, (byte)(enabled ? 1 : 0)) == 0)
            return;

        if (_playbackView == PlaybackView.Manual)
        {
            ReleasePlaybackInput();
            PauseToPreview();
        }
        _settingsWindow.Open(_applicationSettings);
    }

    private bool TryApplyApplicationSettings(ApplicationSettings settings)
    {
        if (!_settingsStore.TrySave(settings))
            return false;

        // Apply localization between frames so all labels refer to the same windows during docking.
        _pendingApplicationSettings = settings;
        return true;
    }
}
