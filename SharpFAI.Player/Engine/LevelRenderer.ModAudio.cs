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

    private bool SyncModAudio()
    {
        var mods = Mods;
        if (mods is null) return false;
        var effects = mods.Entries<RealtimeAudioHook>(ModTarget);
        int generation = mods.Generation;
        if (_modAudioGeneration != generation || _audioNeedsResync || _progressScrubbing || !_isPlaying)
            StopModAudio();
        _modAudioGeneration = generation;
        if (!_audioBackendReady || _settings.SkipAudio || effects.Count == 0 || generation == _modAudioFailureGeneration)
            return false;
        if (!_isPlaying || _progressScrubbing) return true;

        _music?.Pause();
        _hitsound?.Pause();
        var gains = new ModMixGains(_musicEnabled ? PlaybackMusicGain : 0,
            _hitsoundEnabled ? _hitsoundVolume * _hitsoundOutputGain : 0);
        try
        {
            _modAudio ??= new(effects, new(_audioMusic, _audioHitsound, _pitch, 1,
                AudioClockShift, HitsoundClockShift / _pitch), _playTime / _pitch, gains);
            if (_modAudio.Failed)
                throw new InvalidOperationException("The realtime audio worker stopped.");
            _modAudio.SetGains(gains);
            _modAudio.Pump();
            _audioNeedsResync = false;
            return true;
        }
        catch (Exception error)
        {
            mods.Report("audio.process", error.GetBaseException().Message);
            _modAudioFailureGeneration = generation;
            StopModAudio();
            _audioNeedsResync = true;
            return false;
        }
    }

    private void StopModAudio()
    {
        _modAudio?.Dispose();
        _modAudio = null;
    }

    internal DecodedAudio? RenderOfflineModMix(DecodedAudio? music, DecodedAudio? hitsounds, double seconds)
    {
        var effects = Mods?.Entries<RealtimeAudioHook>(UiHost.Export);
        if (effects is not { Count: > 0 }) return null;
        var format = RealtimeModMixer.Format;
        int frames = checked((int)Math.Max(1, Math.Ceiling(seconds * format.SampleRate)));
        var bytes = new byte[checked(frames * format.Channels * sizeof(short))];
        var samples = MemoryMarshal.Cast<byte, short>(bytes.AsSpan());
        var gains = new ModMixGains(MusicVolumeExport, HitsoundVolumeExport);
        using var mixer = new RealtimeModMixer(effects, new(music, hitsounds, PlaybackRate, 1, 0, 0),
            0, offline: true);
        for (int first = 0; first < frames; first += RealtimeModMixer.BlockFrames)
        {
            int count = Math.Min(RealtimeModMixer.BlockFrames, frames - first);
            mixer.Render(first, samples.Slice(first * format.Channels, count * format.Channels), gains);
        }
        return new DecodedAudio { Pcm = bytes, SampleRate = format.SampleRate, Channels = format.Channels };
    }
}
