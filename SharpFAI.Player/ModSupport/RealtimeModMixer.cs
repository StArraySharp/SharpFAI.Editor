using System.Runtime.InteropServices;
using SharpFAI.ModApi;
using Thrust.Audio;

namespace SharpFAI.Player.ModSupport;

internal readonly record struct ModMixSources(DecodedAudio? Music, DecodedAudio? Hitsounds,
    double MusicRate, double HitsoundRate, double MusicOffset, double HitsoundOffset);
internal readonly record struct ModMixGains(float Music, float Hitsounds);

internal sealed class RealtimeModMixer : IDisposable
{
    public static readonly AudioFormat Format = new(48000, 2);
    public const int BlockFrames = 960;
    private sealed record Processor(ModRegistration<RealtimeAudioHook> Entry, IRealtimeAudioProcessor Effect,
        IDisposable Lease)
    {
        public bool Failed { get; set; }
    }

    private readonly Processor[] _processors;
    private readonly float[] _music = new float[BlockFrames * 2];
    private readonly float[] _hitsounds = new float[BlockFrames * 2];
    private readonly float[] _mix = new float[BlockFrames * 2];
    private readonly float[] _backup = new float[BlockFrames * 2];
    private readonly ModMixSources _sources;
    private readonly bool _offline;

    public RealtimeModMixer(IReadOnlyList<ModRegistration<RealtimeAudioHook>> entries,
        ModMixSources sources, long firstFrame, bool offline)
    {
        _offline = offline;
        _sources = sources;
        var processors = new List<Processor>();
        try
        {
            foreach (var entry in entries)
            {
                var lease = entry.Acquire();
                if (lease is null) continue;
                IRealtimeAudioProcessor? effect = null;
                try
                {
                    effect = entry.Value.Create();
                    effect.Reset(Format, firstFrame);
                    processors.Add(new(entry, effect, lease));
                }
                catch (Exception error)
                {
                    try { effect?.Dispose(); }
                    finally { lease.Dispose(); }
                    entry.Report(error);
                    if (_offline && entry.Descriptor.Required) throw;
                }
            }
        }
        catch
        {
            Dispose(processors);
            throw;
        }
        _processors = processors.ToArray();
    }

    public void Render(long firstFrame, Span<short> output, ModMixGains gains)
    {
        var music = _music.AsSpan(0, output.Length);
        var hitsounds = _hitsounds.AsSpan(0, output.Length);
        var mix = _mix.AsSpan(0, output.Length);
        Read(_sources.Music, firstFrame, _sources.MusicRate, _sources.MusicOffset, music);
        Read(_sources.Hitsounds, firstFrame, _sources.HitsoundRate, _sources.HitsoundOffset, hitsounds);
        var block = new RealtimeAudioBlock(Format, firstFrame, _offline);
        Apply(AudioBus.Music, block, music);
        Apply(AudioBus.Hitsounds, block, hitsounds);
        for (int i = 0; i < mix.Length; i++)
            mix[i] = music[i] * gains.Music + hitsounds[i] * gains.Hitsounds;
        Apply(AudioBus.Mix, block, mix);
        for (int i = 0; i < mix.Length; i++)
            output[i] = float.IsFinite(mix[i]) ? (short)MathF.Round(Math.Clamp(mix[i], -1, 1) * 32767) : (short)0;
    }

    private void Apply(AudioBus bus, in RealtimeAudioBlock block, Span<float> samples)
    {
        foreach (var processor in _processors)
        {
            if (processor.Failed || processor.Entry.Value.Bus != bus || !processor.Entry.IsRegistered) continue;
            samples.CopyTo(_backup);
            try { processor.Effect.Process(block, samples); }
            catch (Exception error)
            {
                processor.Failed = true;
                processor.Entry.Report(error);
                _backup.AsSpan(0, samples.Length).CopyTo(samples);
                if (_offline && processor.Entry.Descriptor.Required) throw;
            }
        }
    }

    private static void Read(DecodedAudio? source, long firstFrame, double rate, double offset, Span<float> output)
    {
        output.Clear();
        if (source is null || source.Channels is < 1 or > 2) return;
        var pcm = MemoryMarshal.Cast<byte, short>(source.Pcm.AsSpan());
        int frameCount = pcm.Length / source.Channels;
        double step = rate * source.SampleRate / Format.SampleRate;
        double start = firstFrame * step + offset * source.SampleRate;
        for (int i = 0; i < output.Length / 2; i++)
        {
            double position = start + i * step;
            if (!(position >= 0 && position < frameCount)) continue;
            int frame = (int)position;
            int next = Math.Min(frame + 1, frameCount - 1);
            float fraction = (float)(position - frame);
            for (int channel = 0; channel < 2; channel++)
            {
                int inputChannel = Math.Min(channel, source.Channels - 1);
                short from = pcm[frame * source.Channels + inputChannel];
                short to = pcm[next * source.Channels + inputChannel];
                output[i * 2 + channel] = (from + (to - from) * fraction) / 32768f;
            }
        }
    }

    public void Dispose() => Dispose(_processors);

    private static void Dispose(IEnumerable<Processor> processors)
    {
        foreach (var processor in processors.Reverse())
        {
            try { processor.Effect.Dispose(); }
            catch (Exception error) { processor.Entry.Report(error); }
            finally { processor.Lease.Dispose(); }
        }
    }
}
