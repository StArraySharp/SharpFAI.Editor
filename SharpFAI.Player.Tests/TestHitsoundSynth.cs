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

    [Test]
    public void CoincidentOnsetsDoNotAmplifyTheKick()
    {
        DecodedAudio single = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), [.2], .5);
        DecodedAudio dense = HitsoundSynth.Synthesize(Guid.NewGuid().ToString(), Enumerable.Repeat(.2, 1024).ToArray(), .5);
        Assert.That(dense.Pcm, Is.EqualTo(single.Pcm));
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
