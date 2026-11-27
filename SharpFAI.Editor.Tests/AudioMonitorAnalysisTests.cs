using System.Runtime.InteropServices;
using NUnit.Framework;
using SharpFAI.Editor.Widgets;
using Thrust.Audio;

namespace SharpFAI.Editor.Tests;

public sealed class AudioMonitorAnalysisTests
{
    [Test]
    public void RanksFrequenciesInStereoWithoutCancellingOppositePhases()
    {
        const int rate = 48000;
        short[] stereo = new short[rate * 2];
        for (int i = 0; i < rate; i++)
        {
            double time = (double)i / rate;
            short main = (short)(15000 * Math.Sin(2 * Math.PI * 443 * time));
            short second = (short)(5000 * Math.Sin(2 * Math.PI * 1234 * time));
            stereo[i * 2] = (short)(main + second);
            stereo[i * 2 + 1] = (short)(-main + second);
        }
        var audio = new DecodedAudio
        {
            SampleRate = rate, Channels = 2, Pcm = MemoryMarshal.AsBytes(stereo.AsSpan()).ToArray()
        };
        var analysis = new AudioMonitorAnalysis();
        analysis.Analyze(audio, .5);

        Assert.That(analysis.Peaks[0].Frequency, Is.EqualTo(443).Within(.5));
        Assert.That(analysis.Peaks[1].Frequency, Is.EqualTo(1234).Within(.5));
        Assert.That(analysis.Peaks[0].Level, Is.GreaterThan(analysis.Peaks[1].Level));
        Array.Clear(audio.Pcm);
        analysis.Analyze(audio, audio.Duration);
        Assert.That(analysis.Peaks, Is.Empty);
    }
}
