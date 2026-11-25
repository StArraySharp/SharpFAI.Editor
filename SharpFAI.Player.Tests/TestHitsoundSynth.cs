using System.Buffers.Binary;
using Newtonsoft.Json.Linq;
using SharpFAI.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Audio;
using SharpFAI.Serialization;
using SharpFAI.Util;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public class TestHitsoundSynth
{
    [Test]
    public void MidspinContinuationAtTheSameTimeProducesOneKick()
    {
        var chart = new Level(new Dictionary<string, object>
        {
            ["angleData"] = new JArray(0, 999, 180, 0),
            ["settings"] = new JObject { ["bpm"] = 120, ["separateCountdownTime"] = false },
            ["actions"] = new JArray(),
        });
        var floors = chart.CreateFloors(default, usePositionTrack: false, calculateLegacyEntryTimes: false);
        var timeline = LevelRenderer.BuildTimeline(chart, floors);
        int midspin = floors.FindIndex(floor => floor.isMidspin);
        Assert.That(midspin, Is.GreaterThan(0));
        Assert.That(timeline.EntryTimes[midspin + 1], Is.EqualTo(timeline.EntryTimes[midspin]));
        var keptTimes = timeline.EntryTimes.Where((_, i) => i != midspin + 1).ToArray();
        var expected = HitsoundSynth.SynthesizeTimeline(keptTimes, timeline.Pitch)!;
        var mixed = HitsoundSynth.SynthesizeTimeline(timeline.EntryTimes, timeline.Pitch, floors: floors)!;
        Assert.That(mixed.Pcm, Is.EqualTo(expected.Pcm));
    }

    [TestCase(5000)]
    [TestCase(100000)]
    public void DenseTempoChangesKeepTheHitsoundPeakNearAnIsolatedKick(int fastBpm)
    {
        var chart = new Level(new Dictionary<string, object>
        {
            ["angleData"] = new JArray(Enumerable.Repeat(0, 1080)),
            ["settings"] = new JObject { ["bpm"] = 120, ["separateCountdownTime"] = true },
            ["actions"] = new JArray(Speed(3, fastBpm), Speed(1003, 5000), Speed(1070, 120)),
        });
        List<Floor> floors = chart.CreateFloors(default, usePositionTrack: false, calculateLegacyEntryTimes: false);
        var timeline = LevelRenderer.BuildTimeline(chart, floors);
        double[] entries = timeline.EntryTimes;
        Assert.That(entries[4] - entries[3], Is.EqualTo(60.0 / fastBpm).Within(1e-8));
        Assert.That(entries[1004] - entries[1003], Is.EqualTo(60.0 / 5000).Within(1e-8));

        DecodedAudio audio = LevelRenderer.BuildAudio(chart, Path.Combine(AppContext.BaseDirectory,
            $"{Guid.NewGuid():N}.adofai"), floors, timeline, new LevelPlaybackSettings()).Hitsound!;
        DecodedAudio single = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), [entries[1]], entries[1] + .45);
        int reference = Peak(single, entries[1], entries[1] + .4);
        int stride = audio.Channels * sizeof(short);
        int firstHit = (int)(entries[1] * audio.SampleRate) * stride;
        int isolatedLength = (int)(.4 * audio.SampleRate) * stride;
        Assert.That(reference, Is.GreaterThan(1000));
        Assert.That(audio.Pcm.AsSpan(firstHit, isolatedLength).SequenceEqual(single.Pcm.AsSpan(firstHit, isolatedLength)), Is.True);
        Assert.That(Peak(audio, entries[3], entries[1003]), Is.InRange(100, (int)(reference * 1.30)));
        Assert.That(Peak(audio, entries[1003], entries[1070] + .23), Is.InRange(100, (int)(reference * 1.30)));
    }

    [TestCase(29)]  // About 100,000 BPM at 48 kHz
    [TestCase(576)] // 5,000 BPM at 48 kHz
    public void RapidKickRetainsRhythmicEnergyAndEndsEachVoiceSmoothly(int gap)
    {
        DecodedAudio sample = HitsoundSynth.LoadEmbeddedSample()!;
        const int first = 16375; // The first shortened voice crosses a mixer block boundary.
        double Time(int frame) => (frame + .25) / sample.SampleRate;
        double[] times = Enumerable.Range(0, 32).Select(i => Time(first + i * gap)).ToArray();
        DecodedAudio audio = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), times, times[^1] + .3);
        DecodedAudio single = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), [times[^1]], times[^1] + .3);
        int reference = Peak(single, times[^1], times[^1] + .23);
        int stride = audio.Channels * sizeof(short);
        for (int hit = 0; hit < times.Length - 1; hit++)
        {
            int start = first + hit * gap;
            int Sample(int offset) => BinaryPrimitives.ReadInt16LittleEndian(audio.Pcm.AsSpan((start + offset) * stride));
            Assert.That(Sample(0), Is.Zero);
            Assert.That(Sample(gap - 1), Is.Zero);

            double squareSum = 0, real = 0, imaginary = 0;
            for (int i = 0; i < gap; i++)
            {
                int value = Sample(i);
                (double sine, double cosine) = Math.SinCos(Math.Tau * i / gap);
                squareSum += (double)value * value;
                real += value * cosine;
                imaginary += value * sine;
            }
            Assert.That(Math.Sqrt(squareSum / gap), Is.InRange(reference * .25, reference * .7));
            Assert.That(2 * Math.Sqrt(real * real + imaginary * imaginary) / gap, Is.GreaterThan(reference * .12));
        }

        int tail = (first + (times.Length - 1) * gap) * stride;
        Assert.That(audio.Pcm.AsSpan(tail).SequenceEqual(single.Pcm.AsSpan(tail)), Is.True);
    }

    [TestCase(2, .125f)]
    [TestCase(1024, 1f)]
    public void CoincidentTimelineOnsetsSumBeforePcmSaturation(int hitCount, float volume)
    {
        DecodedAudio kick = HitsoundSynth.LoadEmbeddedSample()!;
        const int first = 200;
        double time = (first + .25) / kick.SampleRate;
        double[] entries = [0, .. Enumerable.Repeat(time, hitCount)];
        DecodedAudio mixed = HitsoundSynth.SynthesizeTimeline(entries, 1, volume)!;
        int startByte = first * kick.Channels * sizeof(short);
        byte[] expected = new byte[kick.Pcm.Length];
        for (int offset = 0; offset < expected.Length; offset += sizeof(short))
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(kick.Pcm.AsSpan(offset));
            short sum = (short)MathF.Round(Math.Clamp(sample * volume * hitCount, short.MinValue, short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(offset), sum);
        }

        Assert.That(mixed.Pcm.AsSpan(startByte, expected.Length).SequenceEqual(expected), Is.True);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(29)]
    [TestCase(1800)]
    public void DistinctOnsetsInTheSameFrameContributeToShortKicks(int gap)
    {
        DecodedAudio kick = HitsoundSynth.LoadEmbeddedSample()!;
        const int first = 16375;
        double Time(int frame, double fraction) => (frame + fraction) / kick.SampleRate;
        int[] frames = Enumerable.Range(0, 8).Select(i => first + i * gap).ToArray();
        double[] single = frames.Select(frame => Time(frame, .25)).ToArray();
        double[] doubled = frames.SelectMany(frame => new[] { Time(frame, .25), Time(frame, .75) }).ToArray();
        double duration = single[^1] + .5;
        DecodedAudio reference = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), single, duration, .25f);
        DecodedAudio mixed = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), doubled, duration, .125f);
        int stride = kick.Channels * sizeof(short);
        byte[] firstHit = mixed.Pcm.AsSpan(first * stride, gap * stride).ToArray();
        Assert.That(firstHit.Any(value => value != 0), Is.True);
        Assert.That(mixed.Pcm, Is.EqualTo(reference.Pcm));
    }

    private static JObject Speed(int floor, int bpm) => new()
    {
        ["floor"] = floor,
        ["eventType"] = "SetSpeed",
        ["speedType"] = "Bpm",
        ["beatsPerMinute"] = bpm,
    };

    private static int Peak(DecodedAudio audio, double start, double end)
    {
        int stride = audio.Channels * sizeof(short);
        int first = (int)(start * audio.SampleRate) * stride;
        int last = Math.Min(audio.Pcm.Length, (int)(end * audio.SampleRate) * stride);
        int peak = 0;
        for (int offset = first; offset < last; offset += sizeof(short))
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.Pcm.AsSpan(offset))));
        return peak;
    }
}
