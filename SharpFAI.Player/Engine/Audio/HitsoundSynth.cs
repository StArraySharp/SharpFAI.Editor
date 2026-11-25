using Thrust;
using Thrust.Audio;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine.Audio;

/// <summary>
/// Hitsound synthesizer: places one hit sample at every note time to build a full track.
/// </summary>
public static class HitsoundSynth
{
    /// <summary>
    /// </summary>
    private const string EmbeddedSampleName = "Resources.kick.wav";

    private static readonly HitsoundPcmCache SynthCache = new(maxBytes: 256L * 1024 * 1024, maxEntries: 8);

    /// <summary>Clear the synthesis cache.</summary>
    public static void ClearCache() => SynthCache.Clear();

    /// <summary>
    /// Synthesize the hitsound track from the given note times, cached, using the embedded kick.
    /// </summary>
    /// <param name="cacheKey">Caller-provided identity for the note times and duration.</param>
    /// <param name="noteTimesSeconds">Note times in seconds.</param>
    /// <param name="totalDuration">Total track length in seconds.</param>
    /// <param name="volume">Sample volume in 0..1.</param>
    public static DecodedAudio Synthesize(
        string cacheKey,
        IReadOnlyList<double> noteTimesSeconds,
        double totalDuration,
        float volume = 0.85f)
        => SynthCache.GetOrAdd(new(cacheKey, volume), () => SynthesizeCore(noteTimesSeconds, totalDuration, volume))!;

    internal static DecodedAudio? SynthesizeTimeline(double[] entryTimes, double rate, float volume = 0.85f,
        IReadOnlyList<Floor>? floors = null)
    {
        if (entryTimes.Length <= 1)
            return null;
        if (floors is not null)
            entryTimes = WithoutMidspinDuplicates(entryTimes, floors);

        // Tile zero never triggers a hit. Hash the remaining times without allocating a scaled list on a hit.
        string timing = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(entryTimes.AsSpan(1))));
        return SynthCache.GetOrAdd(new(timing, volume, BitConverter.DoubleToInt64Bits(rate)), () =>
        {
            List<double> noteTimes = HitsoundTimes(entryTimes);
            if (noteTimes.Count == 0)
                return null;

            // Only the spacing follows the wall-time clock; the sample keeps its original rate.
            for (int i = 0; i < noteTimes.Count; i++)
                noteTimes[i] /= rate;
            return SynthesizeCore(noteTimes, noteTimes[^1] + 0.5, volume);
        });
    }

    private static double[] WithoutMidspinDuplicates(double[] entryTimes, IReadOnlyList<Floor> floors)
    {
        List<double>? times = null;
        for (int i = 1; i < entryTimes.Length; i++)
        {
            // A midspin and its instantaneous continuation are one physical hit.
            if (i > 1 && i - 1 < floors.Count && floors[i - 1].isMidspin && entryTimes[i] == entryTimes[i - 1])
            {
                times ??= [.. entryTimes.AsSpan(0, i)];
                continue;
            }
            times?.Add(entryTimes[i]);
        }
        return times?.ToArray() ?? entryTimes;
    }

    internal static List<double> HitsoundTimes(ReadOnlySpan<double> entryTimes)
    {
        List<double> times = new(entryTimes.Length);
        for (int i = 1; i < entryTimes.Length; i++)
        {
            double time = entryTimes[i];
            if (time >= 0.0)
                times.Add(time);
        }

        return times;
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

        float gain = Math.Clamp(volume, 0f, 1f);
        if (sampleFrames > 0 && gain != 0f)
        {
            float[] kick = new float[sampleFrames * channels];
            for (int i = 0; i < kick.Length; i++)
            {
                kick[i] = BinaryPrimitives.ReadInt16LittleEndian(sample.Pcm.AsSpan(i * sizeof(short))) * gain;
            }

            int[] starts = NoteFrames(noteTimesSeconds, sampleRate, totalFrames);
            RetriggeredKick? retrigger = RetriggeredKick.Prepare(kick, channels, sampleRate, ref starts, out float[]? weights);
            const int blockFrames = 16384;
            int blockCount = (totalFrames - 1) / blockFrames + 1;
            int scratchLength = Math.Min(blockFrames, totalFrames) * channels;
            int fftSize = (int)BitOperations.RoundUpToPowerOf2((uint)(blockFrames + sampleFrames - 1));
            long fftWork = (long)fftSize * BitOperations.Log2((uint)fftSize) * 18;
            Lazy<DenseHitsoundMixer>? dense = channels <= 2 && float.IsFinite(gain)
                ? new(() => new DenseHitsoundMixer(kick, channels, fftSize)) : null;
            if (blockCount == 1)
            {
                MixBlock(starts, weights, kick, channels, 0, totalFrames, new float[scratchLength], pcm, dense, fftWork, retrigger);
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
                    MixBlock(starts, weights, kick, channels, start, count, scratch, pcm, dense, fftWork, retrigger);
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

    private static void MixBlock(ReadOnlySpan<int> noteFrames, ReadOnlySpan<float> weights, ReadOnlySpan<float> kick, int channels,
        int start, int count, Span<float> scratch, Span<byte> pcm, Lazy<DenseHitsoundMixer>? dense, long fftWork,
        RetriggeredKick? retrigger)
    {
        int kickFrames = kick.Length / channels;
        int note = LowerBound(noteFrames, start - kickFrames + 1);
        int end = start + count;
        int lastNote = LowerBound(noteFrames, end);
        int shortHit = retrigger?.FindFirstHit(start, end) ?? -1;
        if (note == lastNote && shortHit < 0)
            return;

        Span<float> mixedBlock = scratch[..(count * channels)];
        if (dense is not null && (long)(lastNote - note) * kick.Length >= fftWork)
        {
            dense.Value.Mix(noteFrames[note..lastNote], start, mixedBlock,
                weights.IsEmpty ? default : weights[note..lastNote]);
        }
        else
        {
            mixedBlock.Clear();
            for (; note < lastNote; note++)
            {
                int hit = noteFrames[note];
                int from = Math.Max(start, hit);
                int length = Math.Min(kickFrames - (from - hit), end - from);
                AddSamples(kick.Slice((from - hit) * channels, length * channels),
                    mixedBlock.Slice((from - start) * channels, length * channels), weights.IsEmpty ? 1f : weights[note]);
            }
        }

        if (shortHit >= 0)
            retrigger!.Mix(shortHit, start, end, mixedBlock);
        WritePcm(mixedBlock, pcm[(start * channels * sizeof(short))..]);
    }

    private static void WritePcm(ReadOnlySpan<float> mixed, Span<byte> bytes)
    {
        for (int i = 0; i < mixed.Length; i++)
        {
            short quantized = (short)MathF.Round(Math.Clamp(mixed[i], short.MinValue, short.MaxValue));
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

    private static void AddSamples(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= source.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(source[i..]) * gain + new Vector<float>(destination[i..])).CopyTo(destination[i..]);
            }
        }
        for (; i < source.Length; i++)
        {
            destination[i] += source[i] * gain;
        }
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
