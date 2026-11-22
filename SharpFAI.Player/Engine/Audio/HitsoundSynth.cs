using Thrust;
using Thrust.Audio;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace SharpFAI.Player.Engine.Audio;

/// <summary>
/// Hitsound synthesizer: places one hit sample at every note time to build a full track.
/// </summary>
public static class HitsoundSynth
{
    /// <summary>
    /// </summary>
    private const string EmbeddedSampleName = "Resources.kick.wav";

    // Synthesized results are cached so each level only builds its track once
    private static readonly Dictionary<string, DecodedAudio> SynthCache = [];
    private static readonly Lock SynthLock = new();

    /// <summary>Clear the synthesis cache.</summary>
    public static void ClearCache()
    {
        lock (SynthLock)
        {
            SynthCache.Clear();
        }
    }

    /// <summary>
    /// Synthesize the hitsound track from the given note times, cached, using the embedded kick.
    /// </summary>
    /// <param name="cacheKey">Cache key, usually the level path.</param>
    /// <param name="noteTimesSeconds">Note times in seconds.</param>
    /// <param name="totalDuration">Total track length in seconds.</param>
    /// <param name="volume">Sample volume in 0..1.</param>
    public static DecodedAudio Synthesize(
        string cacheKey,
        IReadOnlyList<double> noteTimesSeconds,
        double totalDuration,
        float volume = 0.85f)
    {
        lock (SynthLock)
        {
            if (SynthCache.TryGetValue(cacheKey, out DecodedAudio? cached))
            {
                return cached;
            }
        }

        DecodedAudio result = SynthesizeCore(noteTimesSeconds, totalDuration, volume);

        lock (SynthLock)
        {
            SynthCache[cacheKey] = result;
        }

        return result;
    }

    /// <summary>Load the embedded kick sample.</summary>
    public static DecodedAudio? LoadEmbeddedSample()
    {
        System.Reflection.Assembly assembly = typeof(HitsoundSynth).Assembly;

        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);

            // Use the resource name as the cache key so it only decodes once
            return AudioDecoder.Decode(buffer.ToArray(), "embedded:" + name, ".wav");
        }

        return null;
    }

    private static DecodedAudio SynthesizeCore(
        IReadOnlyList<double> noteTimesSeconds,
        double totalDuration,
        float volume)
    {
        DecodedAudio sample = LoadEmbeddedSample() ?? CreateBuiltInClick(DecodedAudio.TargetSampleRate);
        int sampleRate = sample.SampleRate > 0 ? sample.SampleRate : DecodedAudio.TargetSampleRate;
        int channels = Math.Max(sample.Channels, 1);
        int sampleFrames = sample.Pcm.Length / (channels * sizeof(short));
        int totalFrames = Math.Max(1, (int)Math.Ceiling(totalDuration * sampleRate));
        byte[] pcm = new byte[checked(totalFrames * channels * sizeof(short))];

        float gain = Math.Clamp(volume, 0f, 1f) / short.MaxValue;
        if (sampleFrames > 0 && gain != 0f)
        {
            float[] kick = new float[sampleFrames * channels];
            for (int i = 0; i < kick.Length; i++)
            {
                kick[i] = BinaryPrimitives.ReadInt16LittleEndian(sample.Pcm.AsSpan(i * sizeof(short))) * gain;
            }

            int[] starts = NoteFrames(noteTimesSeconds, sampleRate, totalFrames);
            const int blockFrames = 16384;
            int blockCount = (totalFrames - 1) / blockFrames + 1;
            int scratchLength = Math.Min(blockFrames, totalFrames) * channels;
            int fftSize = (int)BitOperations.RoundUpToPowerOf2((uint)(blockFrames + sampleFrames - 1));
            long fftWork = (long)fftSize * BitOperations.Log2((uint)fftSize) * 18;
            Lazy<DenseHitsoundMixer>? dense = channels <= 2 && float.IsFinite(gain)
                ? new(() => new DenseHitsoundMixer(kick, channels, fftSize)) : null;
            if (blockCount == 1)
            {
                MixBlock(starts, kick, channels, 0, totalFrames, new float[scratchLength], pcm, dense, fftWork);
            }
            else if (starts.Length > 0)
            {
                Parallel.For(0, blockCount, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
                },
                () => ArrayPool<float>.Shared.Rent(scratchLength),
                (block, _, scratch) =>
                {
                    int start = block * blockFrames;
                    int count = Math.Min(blockFrames, totalFrames - start);
                    MixBlock(starts, kick, channels, start, count, scratch, pcm, dense, fftWork);
                    return scratch;
                },
                scratch => ArrayPool<float>.Shared.Return(scratch));
            }
        }

        return new DecodedAudio { Pcm = pcm, Channels = channels, SampleRate = sampleRate };
    }

    private static int[] NoteFrames(IReadOnlyList<double> times, int sampleRate, int totalFrames)
    {
        List<int> frames = new(times.Count);
        bool ordered = true;
        foreach (double time in times)
        {
            double position = time * sampleRate;
            if (!(position > -1 && position < totalFrames))
            {
                continue;
            }

            int frame = (int)position;
            ordered &= frames.Count == 0 || frame >= frames[^1];
            frames.Add(frame);
        }

        if (!ordered)
        {
            frames.Sort();
        }

        return frames.ToArray();
    }

    private static void MixBlock(ReadOnlySpan<int> noteFrames, ReadOnlySpan<float> kick, int channels,
        int start, int count, Span<float> scratch, Span<byte> pcm, Lazy<DenseHitsoundMixer>? dense, long fftWork)
    {
        int kickFrames = kick.Length / channels;
        int note = LowerBound(noteFrames, start - kickFrames + 1);
        int end = start + count;
        if (note == noteFrames.Length || noteFrames[note] >= end)
        {
            return;
        }

        int lastNote = LowerBound(noteFrames, end);
        if (dense is not null && (long)(lastNote - note) * kick.Length >= fftWork)
        {
            Span<float> mixedBlock = scratch[..(count * channels)];
            dense.Value.Mix(noteFrames[note..lastNote], start, mixedBlock);
            WritePcm(mixedBlock, pcm[(start * channels * sizeof(short))..]);
            return;
        }

        scratch[..(count * channels)].Clear();
        int firstWritten = Math.Max(start, noteFrames[note]);
        int lastWritten = firstWritten;
        for (; note < noteFrames.Length && noteFrames[note] < end; note++)
        {
            int hit = noteFrames[note];
            int from = Math.Max(start, hit);
            int length = Math.Min(kickFrames - (from - hit), end - from);
            AddSamples(kick.Slice((from - hit) * channels, length * channels),
                scratch.Slice((from - start) * channels, length * channels));
            lastWritten = Math.Max(lastWritten, from + length);
        }

        ReadOnlySpan<float> mixed = scratch.Slice((firstWritten - start) * channels, (lastWritten - firstWritten) * channels);
        WritePcm(mixed, pcm[(firstWritten * channels * sizeof(short))..]);
    }

    private static void WritePcm(ReadOnlySpan<float> mixed, Span<byte> bytes)
    {
        for (int i = 0; i < mixed.Length; i++)
        {
            short quantized = (short)(FastTanh(mixed[i]) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes[(i * sizeof(short))..], quantized);
        }
    }

    private static int LowerBound(ReadOnlySpan<int> frames, int target)
    {
        int lo = 0, hi = frames.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (frames[mid] < target)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private static void AddSamples(ReadOnlySpan<float> source, Span<float> destination)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(source[i..]) + new Vector<float>(destination[i..])).CopyTo(destination[i..]);
            }
        }
        for (; i < source.Length; i++)
        {
            destination[i] += source[i];
        }
    }

    /// <summary>
    /// Fast tanh approximation (error below 0.001), plenty accurate for soft clipping.
    /// </summary>
    private static float FastTanh(float x)
    {
        if (x < -3f)
        {
            return -1f;
        }

        if (x > 3f)
        {
            return 1f;
        }

        float x2 = x * x;
        return x * (27f + x2) / (27f + 9f * x2);
    }

    /// <summary>
    /// Built-in click: a short decaying sine with a noise transient, no external asset needed.
    /// </summary>
    private static DecodedAudio CreateBuiltInClick(int sampleRate)
    {
        const double duration = 0.09;
        const int channels = 2;

        int frames = (int)(sampleRate * duration);
        byte[] pcm = new byte[frames * channels * sizeof(short)];

        Random random = new(12345);
        double frequency = 880.0;
        double decay = 32.0;

        for (int frame = 0; frame < frames; frame++)
        {
            double t = frame / (double)sampleRate;
            double envelope = Math.Exp(-decay * t);

            // Body: decaying sine
            double tone = Math.Sin(2.0 * Math.PI * frequency * t) * envelope;

            // Transient: noise during the first 4 ms makes the hit punchier
            double noise = 0.0;
            if (t < 0.004)
            {
                noise = (random.NextDouble() * 2.0 - 1.0) * (1.0 - t / 0.004) * 0.6;
            }

            double value = Math.Clamp(tone * 0.75 + noise, -1.0, 1.0);
            short quantized = (short)(value * short.MaxValue * 0.8);

            for (int channel = 0; channel < channels; channel++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcm.AsSpan((frame * channels + channel) * sizeof(short)), quantized);
            }
        }

        return new DecodedAudio
        {
            Pcm = pcm,
            Channels = channels,
            SampleRate = sampleRate,
        };
    }
}
