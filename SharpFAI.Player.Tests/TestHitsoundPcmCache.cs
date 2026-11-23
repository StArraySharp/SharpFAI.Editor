using SharpFAI.Player.Engine.Audio;
using Thrust.Audio;

namespace SharpFAI.Player.Tests;

public class TestHitsoundPcmCache
{
    [TestCase(8, 3)]
    [TestCase(100, 2)]
    public void EvictsLeastRecentlyUsedWhenEitherLimitIsReached(long maxBytes, int maxEntries)
    {
        var cache = new HitsoundPcmCache(maxBytes, maxEntries);
        int builds = 0;
        DecodedAudio? Get(string key) => cache.GetOrAdd(new(key, 1f), () =>
        {
            builds++;
            return new DecodedAudio { Pcm = new byte[4] };
        });

        var a = Get("a");
        var b = Get("b");
        Assert.That(Get("a"), Is.SameAs(a));
        Get("c");
        Assert.That(Get("a"), Is.SameAs(a));
        Assert.That(Get("b"), Is.Not.SameAs(b));
        Assert.That(builds, Is.EqualTo(4));
    }

    [Test]
    public void OversizeTrackIsReturnedWithoutDisplacingCachedAudio()
    {
        var cache = new HitsoundPcmCache(maxBytes: 4, maxEntries: 2);
        DecodedAudio? Get(string key, int bytes) => cache.GetOrAdd(new(key, 1f),
            () => new DecodedAudio { Pcm = new byte[bytes] });

        var cached = Get("small", 4);
        var oversize = Get("large", 6);
        Assert.That(oversize?.Pcm.Length, Is.EqualTo(6));
        Assert.That(Get("large", 6), Is.Not.SameAs(oversize));
        Assert.That(Get("small", 4), Is.SameAs(cached));
        cache.Clear();
        Assert.That(Get("small", 4), Is.Not.SameAs(cached));
    }

    [Test]
    public void FailedSynthesisCanBeRetried()
    {
        var cache = new HitsoundPcmCache(maxBytes: 4, maxEntries: 2);
        var key = new HitsoundPcmCache.Key("retry", 1f);
        Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd(key, () => throw new InvalidOperationException()));
        var audio = new DecodedAudio { Pcm = new byte[4] };
        Assert.That(cache.GetOrAdd(key, () => audio), Is.SameAs(audio));
    }
}
