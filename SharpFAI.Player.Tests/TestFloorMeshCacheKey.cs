using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

/// <summary>
/// Guards the tile-shape cache: a level has a handful of distinct shapes, so the cache must stay tiny
/// no matter how many floors there are. A key that varies with floating-point noise would allocate one
/// entry per floor and grow without bound, which is what a large chart exposed.
/// </summary>
[TestFixture]
public class TestFloorMeshCacheKey
{
    private const string BigLevelPath =
        @"c:\Users\StArray\Downloads\Arche - かめりあ (Camellia)\[𝐃𝐎 𝐍𝐎𝐓 𝐎𝐏𝐄𝐍 𝐓𝐇𝐈𝐒 𝐀𝐓 𝐇𝐎𝐌𝐄] FULL.adofai";

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
    public void CacheStaysSmallForHugeLevel()
    {
        if (!File.Exists(BigLevelPath))
        {
            Assert.Ignore($"Level not found: {BigLevelPath}");
            return;
        }

        FloorMesh.ClearCache();

        Stopwatch sw = Stopwatch.StartNew();
        Level level = new(BigLevelPath);
        List<Floor> floors = level.CreateFloors(usePositionTrack: true);
        sw.Stop();

        TestContext.Out.WriteLine($"floors = {floors.Count:N0} in {sw.Elapsed.TotalMilliseconds:N0} ms");

        // Bake every floor exactly the way the renderer does.
        HashSet<string> keys = [];
        sw.Restart();

        foreach (Floor floor in floors)
        {
            FloorMesh mesh = new(
                floor.entryAngle,
                floor.exitAngle,
                Floor.width / 100f,
                Floor.length / 100f,
                floor.isMidspin ? 3 : 40);

            keys.Add(mesh.CacheKey);
            _ = mesh.Build();
        }

        sw.Stop();

        TestContext.Out.WriteLine($"distinct keys = {keys.Count:N0}, cache = {FloorMesh.CacheCount:N0}");
        TestContext.Out.WriteLine($"bake = {sw.Elapsed.TotalMilliseconds:N0} ms");

        Assert.That(keys.Count, Is.LessThan(200),
            $"A level should have a handful of shapes, found {keys.Count:N0} distinct keys");
        Assert.That(FloorMesh.CacheCount, Is.LessThan(200),
            $"Cache must stay bounded, holds {FloorMesh.CacheCount:N0} entries");
    }
}
