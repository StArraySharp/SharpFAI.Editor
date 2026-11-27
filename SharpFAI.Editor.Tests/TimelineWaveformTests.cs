using System.Runtime.InteropServices;
using NUnit.Framework;
using SharpFAI.Editor.Widgets;
using Thrust.Audio;

namespace SharpFAI.Editor.Tests;

public sealed class TimelineWaveformTests
{
    [Test]
    public void MeasuresStereoPeaksAtExactSampleBoundariesAcrossCachedBlocks()
    {
        const int sampleRate = 1024;
        short[] stereo = new short[600 * 2];
        stereo[19 * 2] = -2000;
        stereo[19 * 2 + 1] = 3000;
        stereo[145 * 2 + 1] = 7000;
        stereo[380 * 2] = -6000;
        stereo[599 * 2] = 11000;
        var audio = new DecodedAudio { SampleRate = sampleRate, Channels = 2, Pcm = MemoryMarshal.AsBytes(stereo.AsSpan()).ToArray() };
        var wave = TimelineWaveform.Build(audio, CancellationToken.None);

        Assert.That(wave.Measure(19d / sampleRate, 20d / sampleRate), Is.EqualTo(new TimelineWaveform.Peak(-2000, 3000)));
        Assert.That(wave.Measure(20d / sampleRate, 500d / sampleRate), Is.EqualTo(new TimelineWaveform.Peak(-6000, 7000)));
        Assert.That(wave.Measure(599d / sampleRate, 2), Is.EqualTo(new TimelineWaveform.Peak(0, 11000)));
        Assert.That(wave.Measure(1, 2), Is.EqualTo(TimelineWaveform.Peak.Empty));
    }
}
