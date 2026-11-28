using System.Numerics;
using System.Runtime.InteropServices;
using Thrust.Audio;

namespace SharpFAI.Editor.Widgets;

internal sealed class TimelineWaveform
{
    private const int MinimumBlockFrames = 64;
    private const int MaximumBlocks = 1 << 19;
    private readonly byte[]? _pcm;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly long _frames;
    private readonly int _blockFrames;
    private readonly Peak[][] _levels;

    internal readonly record struct Peak(short Min, short Max)
    {
        public static Peak Empty => new(short.MaxValue, short.MinValue);
        public Peak Include(Peak other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
    }

    private TimelineWaveform(DecodedAudio music, long frames, int blockFrames, Peak[][] levels)
    {
        _pcm = music.IsStreamed ? null : music.Pcm;
        _sampleRate = music.SampleRate;
        _channels = music.Channels;
        _frames = frames;
        _blockFrames = blockFrames;
        _levels = levels;
    }

    public static TimelineWaveform Build(DecodedAudio music, CancellationToken cancellation)
    {
        long frames = music.FrameCount;
        int blockFrames = (int)Math.Max(MinimumBlockFrames, (frames + MaximumBlocks - 1) / MaximumBlocks);
        var first = new Peak[(frames + blockFrames - 1) / blockFrames];
        const int chunkFrames = 8192;
        var samples = new short[chunkFrames * music.Channels];
        using var reader = music.OpenReader();
        long scanned = 0;
        int block = 0, inBlock = 0;
        Peak peak = Peak.Empty;
        while (block < first.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = reader.Read(samples);
            if (read == 0) break;
            scanned += read;
            for (int start = 0; start < read;)
            {
                int count = Math.Min(read - start, blockFrames - inBlock);
                peak = peak.Include(Read(samples.AsSpan(start * music.Channels, count * music.Channels)));
                inBlock += count;
                start += count;
                if (inBlock == blockFrames)
                {
                    first[block++] = peak;
                    peak = Peak.Empty;
                    inBlock = 0;
                }
            }
        }
        if (inBlock > 0) first[block++] = peak;
        if (block != first.Length) Array.Resize(ref first, block);

        var levels = new List<Peak[]> { first };
        while (levels[^1].Length > 1)
        {
            cancellation.ThrowIfCancellationRequested();
            var lower = levels[^1];
            var upper = new Peak[(lower.Length + 1) / 2];
            for (int i = 0; i < lower.Length / 2; i++)
                upper[i] = lower[i * 2].Include(lower[i * 2 + 1]);
            if (lower.Length % 2 != 0) upper[^1] = lower[^1];
            levels.Add(upper);
        }
        return new(music, scanned, blockFrames, levels.ToArray());
    }

    public Peak Measure(double startTime, double endTime)
    {
        long start = (long)Math.Clamp(Math.Floor(startTime * _sampleRate), 0, _frames);
        long end = (long)Math.Clamp(Math.Ceiling(endTime * _sampleRate), 0, _frames);
        if (start >= end) return Peak.Empty;

        // Streamed tracks keep only peak blocks. Include the edge blocks conservatively.
        if (_pcm is null)
            return MeasureBlocks((int)(start / _blockFrames), (int)((end + _blockFrames - 1) / _blockFrames));

        var samples = MemoryMarshal.Cast<byte, short>(_pcm.AsSpan());
        int firstBlock = (int)((start + _blockFrames - 1) / _blockFrames);
        int endBlock = (int)(end / _blockFrames);
        if (firstBlock >= endBlock)
            return Read(samples[(int)(start * _channels)..(int)(end * _channels)]);

        return Read(samples[(int)(start * _channels)..(int)((long)firstBlock * _blockFrames * _channels)])
            .Include(MeasureBlocks(firstBlock, endBlock))
            .Include(Read(samples[(int)((long)endBlock * _blockFrames * _channels)..(int)(end * _channels)]));
    }

    private Peak MeasureBlocks(int start, int end)
    {
        Peak result = Peak.Empty;
        while (start < end)
        {
            int level = Math.Min(BitOperations.TrailingZeroCount((uint)start), BitOperations.Log2((uint)(end - start)));
            result = result.Include(_levels[level][start >> level]);
            start += 1 << level;
        }
        return result;
    }

    private static Peak Read(ReadOnlySpan<short> samples)
    {
        short min = short.MaxValue, max = short.MinValue;
        foreach (short sample in samples)
        {
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
        }
        return new(min, max);
    }
}
