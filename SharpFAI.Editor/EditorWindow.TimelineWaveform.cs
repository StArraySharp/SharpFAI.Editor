using SharpFAI.Editor.Widgets;
using Thrust.Audio;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private DecodedAudio? _timelineWaveformSource;
    private Task<TimelineWaveform>? _timelineWaveformBuild;
    private CancellationTokenSource? _timelineWaveformCancellation;

    private void UpdateTimelineWaveform()
    {
        var music = _displayedRevision?.Document == _document
            ? HasCurrentPlayback ? _playback!.AudioSources.Music : _timelineWaveformSource
            : null;
        if (music is not { Channels: 1 or 2, SampleRate: > 0, Pcm.Length: > 0 }) music = null;
        if (!ReferenceEquals(music, _timelineWaveformSource))
        {
            CancelTimelineWaveform();
            _timelineWaveformSource = music;
        }

        if (_showTimeline && music is not null && _timelineWaveformBuild is null)
        {
            _timelineWaveformCancellation = new CancellationTokenSource();
            var token = _timelineWaveformCancellation.Token;
            _timelineWaveformBuild = Task.Run(() => TimelineWaveform.Build(music, token), token);
        }
        if (_timelineWaveformBuild is { IsCompletedSuccessfully: true } completed)
            _chartTimeline.Waveform = completed.Result;
    }

    private void CancelTimelineWaveform()
    {
        _timelineWaveformCancellation?.Cancel();
        _timelineWaveformCancellation?.Dispose();
        _timelineWaveformCancellation = null;
        _timelineWaveformBuild = null;
        _timelineWaveformSource = null;
        _chartTimeline.Waveform = null;
    }
}
