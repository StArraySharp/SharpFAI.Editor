using System.Numerics;
using System.Runtime.InteropServices;
using Thrust.Audio;

namespace SharpFAI.Editor.Widgets;

internal sealed class TimelineWaveform
{
    private const int FramesPerBlock = 64;
    private readonly byte[] _pcm;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _frames;
    private readonly Peak[][] _levels;

    internal readonly record struct Peak(short Min, short Max)
    {
        public static Peak Empty => new(short.MaxValue, short.MinValue);
        public Peak Include(Peak other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
    }

    private TimelineWaveform(DecodedAudio music, int frames, Peak[][] levels)
    {
        _pcm = music.Pcm;
        _sampleRate = music.SampleRate;
        _channels = music.Channels;
        _frames = frames;
        _levels = levels;
    }

    public static TimelineWaveform Build(DecodedAudio music, CancellationToken cancellation)
    {
        var samples = MemoryMarshal.Cast<byte, short>(music.Pcm.AsSpan());
        int frames = samples.Length / music.Channels;
        var first = new Peak[(frames + FramesPerBlock - 1) / FramesPerBlock];
        for (int block = 0; block < first.Length; block++)
        {
            if ((block & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int start = block * FramesPerBlock;
            int end = Math.Min(frames, start + FramesPerBlock);
            first[block] = Read(samples, music.Channels, start, end);
        }
        var levels = new List<Peak[]> { first };
        while (levels[^1].Length > 1)
        {
            cancellation.ThrowIfCancellationRequested();
            var lower = levels[^1];
            var upper = new Peak[(lower.Length + 1) / 2];
            for (int block = 0; block < lower.Length / 2; block++)
                upper[block] = lower[block * 2].Include(lower[block * 2 + 1]);
            if (lower.Length % 2 != 0) upper[^1] = lower[^1];
            levels.Add(upper);
        }
        return new(music, frames, levels.ToArray());
    }

    public Peak Measure(double startTime, double endTime)
    {
        int start = (int)Math.Clamp(Math.Floor(startTime * _sampleRate), 0, _frames);
        int end = (int)Math.Clamp(Math.Ceiling(endTime * _sampleRate), 0, _frames);
        if (start >= end) return Peak.Empty;

        var samples = MemoryMarshal.Cast<byte, short>(_pcm.AsSpan());
        int firstBlock = (start + FramesPerBlock - 1) / FramesPerBlock;
        int endBlock = end / FramesPerBlock;
        if (firstBlock >= endBlock) return Read(samples, _channels, start, end);

        // The cached blocks cover the interior. Read raw samples on either edge for accurate zoomed views.
        Peak result = Read(samples, _channels, start, firstBlock * FramesPerBlock)
            .Include(Read(samples, _channels, endBlock * FramesPerBlock, end));
        while (firstBlock < endBlock)
        {
            int level = Math.Min(BitOperations.TrailingZeroCount((uint)firstBlock), BitOperations.Log2((uint)(endBlock - firstBlock)));
            result = result.Include(_levels[level][firstBlock >> level]);
            firstBlock += 1 << level;
        }
        return result;
    }

    private static Peak Read(ReadOnlySpan<short> samples, int channels, int start, int end)
    {
        short min = short.MaxValue, max = short.MinValue;
        foreach (short sample in samples[(start * channels)..(end * channels)])
        {
            min = Math.Min(min, sample);
            max = Math.Max(max, sample);
        }
        return new(min, max);
    }
}
