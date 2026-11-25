using System.Collections.Generic;
using NUnit.Framework;
using SharpFAI.Player.Engine;

namespace SharpFAI.Player.Tests;

/// <summary>
/// Guards the tile-shape cache: a level has a handful of distinct shapes, so the cache must stay tiny
/// no matter how many floors there are. A key that varies with floating-point noise would allocate one
/// entry per floor and grow without bound, which is what a large chart exposed.
/// </summary>
[TestFixture]
public class TestFloorMeshCacheKey
{
    [Test]
    public void KeyIsStableAgainstFloatingPointNoise()
    {
        // Two headings that differ only in the last bits of a double must share one key.
        FloorMesh a = new(0.0, 270.0);
        FloorMesh b = new(1e-14, 270.00000000000006);
        FloorMesh c = new(360.0, 270.0 + 1e-13);

        Assert.That(a.CacheKey, Is.EqualTo(b.CacheKey),
            "Angles differing by floating-point noise must collapse to one key");
        Assert.That(a.CacheKey, Is.EqualTo(c.CacheKey),
            "0 and 360 must share a key");

        TestContext.Out.WriteLine($"key = {a.CacheKey}");
    }

    [Test]
    public void KeyDistinguishesRealShapes()
    {
        FloorMesh straight = new(0.0, 0.0);
        FloorMesh right = new(0.0, 90.0);

        Assert.That(straight.CacheKey, Is.Not.EqualTo(right.CacheKey),
            "Genuinely different shapes must not collide");
    }

    [Test]
    public void CacheStaysSmallForRepeatedShapesWithFloatingPointNoise()
    {
        FloorMesh.ClearCache();
        HashSet<string> keys = [];
        for (int i = 0; i < 20_000; i++)
        {
            double noise = (i / 8 + 1) * 1e-14;
            var mesh = new FloorMesh(45d * (i % 8) + noise, 90d + noise);
            keys.Add(mesh.CacheKey);
            _ = mesh.Build();
        }

        Assert.That(keys, Has.Count.EqualTo(8));
        Assert.That(FloorMesh.CacheCount, Is.EqualTo(keys.Count));
    }
}
