using System.Runtime.InteropServices;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;
using Thrust.Audio;

namespace SharpFAI.Player.Engine;

public sealed partial class LevelRenderer
{
    private RealtimeModAudioStream? _modAudio;
    private int _modAudioGeneration = -1;
    private int _modAudioFailureGeneration = -1;
    private MetronomeSamples? _editorMetronome;

    internal void SetEditorMetronome(MetronomeSamples? metronome)
    {
        if (_editorMetronome == metronome) return;
        _editorMetronome = metronome;
        if (metronome is not null) _modAudioFailureGeneration = -1;
        _audioNeedsResync = true;
        StopModAudio();
    }

    // Null leaves output to the native tracks; otherwise the result says whether the chart can advance.
    private bool? SyncModAudio()
    {
        var mods = Mods;
        var effects = mods?.Entries<RealtimeAudioHook>(ModTarget) ?? [];
        var metronome = _editorMetronome;
        int generation = mods?.Generation ?? 0;
        if (_modAudioGeneration != generation || _audioNeedsResync || _progressScrubbing || !_isPlaying)
            StopModAudio();
        _modAudioGeneration = generation;
        if (!_audioBackendReady || _settings.SkipAudio || (effects.Count == 0 && metronome is null) || generation == _modAudioFailureGeneration)
            return null;
        if (!_isPlaying || _progressScrubbing) return true;

        _music?.Pause();
        _hitsound?.Pause();
        var gains = new ModMixGains(_musicEnabled ? PlaybackMusicGain : 0,
            _hitsoundEnabled ? _hitsoundVolume * _hitsoundOutputGain : 0, _playbackOutputGain);
        try
        {
            _modAudio ??= new(effects, new(_audioMusic, _audioHitsound, _pitch, 1,
                AudioClockShift, HitsoundClockShift / _pitch, metronome), _playTime / _pitch, gains);
            if (_modAudio.Failed)
                throw new InvalidOperationException("The realtime audio worker stopped.");
            _modAudio.SetGains(gains);
            bool alreadyPlaying = _modAudio.Pump();
            _audioNeedsResync = false;
            return metronome is null || alreadyPlaying;
        }
        catch (Exception error)
        {
            string message = error.GetBaseException().Message;
            if (mods is null) Console.Error.WriteLine($"[Audio] {message}");
            else mods.Report("audio.process", message);
            _modAudioFailureGeneration = generation;
            StopModAudio();
            _audioNeedsResync = true;
            return null;
        }
    }

    private void StopModAudio()
    {
        _modAudio?.Dispose();
        _modAudio = null;
    }

    internal DecodedAudio? RenderOfflineModMix(DecodedAudio? music, DecodedAudio? hitsounds, double seconds,
        double musicStart, double hitsoundStart, double preroll)
    {
        var effects = Mods?.Entries<RealtimeAudioHook>(UiHost.Export);
        if (effects is not { Count: > 0 }) return null;
        var format = RealtimeModMixer.Format;
        int frames = checked((int)Math.Max(1, Math.Ceiling(seconds * format.SampleRate)));
        var bytes = new byte[checked(frames * format.Channels * sizeof(short))];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        var gains = new ModMixGains(MusicVolumeExport, HitsoundVolumeExport);
        long startFrame = (long)(hitsoundStart * format.SampleRate);
        long warmupStart = Math.Max(0, startFrame - (long)(preroll * format.SampleRate));
        double clockStart = startFrame / (double)format.SampleRate;
        using var mixer = new RealtimeModMixer(effects,
            new(music, hitsounds, PlaybackRate, 1, musicStart - clockStart * PlaybackRate, hitsoundStart - clockStart),
            warmupStart, offline: true);
        if (warmupStart < startFrame)
        {
            Span<short> scratch = stackalloc short[RealtimeModMixer.BlockFrames * format.Channels];
            for (long first = warmupStart; first < startFrame; first += RealtimeModMixer.BlockFrames)
            {
                int count = (int)Math.Min(RealtimeModMixer.BlockFrames, startFrame - first);
                mixer.Render(first, scratch[..(count * format.Channels)], gains);
            }
        }
        for (int first = 0; first < frames; first += RealtimeModMixer.BlockFrames)
        {
            int count = Math.Min(RealtimeModMixer.BlockFrames, frames - first);
            mixer.Render(startFrame + first, samples.Slice(first * format.Channels, count * format.Channels), gains);
        }
        return new DecodedAudio { Pcm = bytes, SampleRate = format.SampleRate, Channels = format.Channels };
    }
}
