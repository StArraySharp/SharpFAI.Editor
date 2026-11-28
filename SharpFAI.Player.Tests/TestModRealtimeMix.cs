using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public sealed class TestModRealtimeMix
{
    [Test]
    public void DspProcessesStereoStemAndMixWithFixedSampleClock()
    {
        var owner = new Owner();
        var stem = new Gain(.5f);
        var mix = new Gain(.25f);
        ModRegistration<RealtimeAudioHook>[] entries =
        [
            new(owner, new("test/mix"), new(AudioBus.Mix, () => mix)),
            new(owner, new("test/music"), new(AudioBus.Music, () => stem)),
        ];
        byte[] pcm = new byte[128];
        for (int i = 0; i < pcm.Length; i += 2)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), 16384);
        var track = new DecodedAudio { Pcm = pcm, SampleRate = 48000, Channels = 1 };
        using (var renderer = new RealtimeModMixer(entries, new(track, track, 1, 1, 0, 0), 7, offline: false))
        {
            Span<short> samples = stackalloc short[8];
            renderer.Render(7, samples, new(1, 1));
            Assert.That(samples.ToArray(), Is.All.EqualTo((short)6144));
            Assert.That(stem.Start, Is.EqualTo(7));
            Assert.That(mix.Start, Is.EqualTo(7));
            Assert.That(owner.Active, Is.EqualTo(2));
        }
        Assert.That(owner.Active, Is.Zero);
    }

    [Test]
    public void MetronomeUsesTheMusicClockInTheLiveMixAndIgnoresStemGains()
    {
        const long frame = 12345;
        void Clicks(long firstFrame, double step, double offset, Span<float> samples)
        {
            Assert.That(firstFrame, Is.EqualTo(frame));
            Assert.That(step, Is.EqualTo(.5 / 48000));
            Assert.That(offset, Is.EqualTo(-.02));
            samples.Fill(.5f);
        }

        var sources = new ModMixSources(null, null, .5, 1, -.02, 0, Clicks);
        using var live = new RealtimeModMixer([], sources, frame, offline: false);
        using var export = new RealtimeModMixer([], sources, frame, offline: true);
        short[] samples = new short[8];
        live.Render(frame, samples, new(0, 0, .5f));
        Assert.That(samples, Is.All.EqualTo(8192));
        live.Render(frame, samples, new(0, 0, 0));
        Assert.That(samples, Is.All.Zero);
        export.Render(frame, samples, new(0, 0, .5f));
        Assert.That(samples, Is.All.Zero);
    }

    private sealed class Gain(float scale) : IRealtimeAudioProcessor
    {
        public long Start { get; private set; }
        public void Reset(AudioFormat format, long firstFrame) => Start = firstFrame;
        public void Process(in RealtimeAudioBlock block, Span<float> interleavedSamples)
        {
            Assert.That(block.Format, Is.EqualTo(new AudioFormat(48000, 2)));
            foreach (ref float value in interleavedSamples) value *= scale;
        }
        public void Dispose() { }
    }

    private sealed class Owner : IModRegistrationOwner
    {
        public string Id => "test";
        public bool Stopping => false;
        public CancellationToken Lifetime => default;
        public int Active { get; private set; }
        public IDisposable Acquire() { Active++; return new Lease(this); }
        public string Text(string key, string? fallback) => fallback ?? key;
        public void Log(string message) => Assert.Fail(message);
        private sealed class Lease(Owner owner) : IDisposable
        {
            public void Dispose() => owner.Active--;
        }
    }
}
