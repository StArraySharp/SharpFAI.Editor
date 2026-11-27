using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private readonly AudioMonitorWindow _audioMonitor = new();

    private void DrawAudioMonitor()
    {
        var renderer = _displayedRevision?.Document == _document ? _playback : null;
        var music = _audioMonitor.IsOpen ? renderer?.AudioSources.Music : null;
        _audioMonitor.Draw(music, renderer?.PlayTime ?? 0, renderer?.IsPlaying == true);
    }
}
