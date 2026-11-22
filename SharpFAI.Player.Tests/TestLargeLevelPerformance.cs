using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SharpFAI.Framework;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Player.Tests;

/// <summary>
/// 大规模关卡的解析性能测试 / Performance profiling for very large levels.
/// </summary>
[TestFixture]
public class TestLargeLevelPerformance
{
    private const string BigLevelPath = @"D:\ADOFAILevels\已完成\cintptr\level.adofai";

    private static void Report(string phase, Stopwatch sw)
    {
        TestContext.Out.WriteLine($"{phase,-16} {sw.Elapsed.TotalMilliseconds,10:0.0} ms");
    }

    [Test]
    public void ProfileLargeLevel()
    {
        if (!System.IO.File.Exists(BigLevelPath))
        {
            Assert.Ignore($"未找到大关卡 / Large level not found: {BigLevelPath}");
            return;
        }

        Stopwatch total = Stopwatch.StartNew();

        Stopwatch sw = Stopwatch.StartNew();
        Level level = new(BigLevelPath);
        sw.Stop();
        Report("解析关卡", sw);
        TestContext.Out.WriteLine($"  angles = {level.angles.Count:N0}");

        sw.Restart();
        var noteTimes = level.GetNoteTimesNew();
        sw.Stop();
        Report("计算时间轴", sw);
        TestContext.Out.WriteLine($"  noteTimes = {noteTimes.Count:N0}");

        sw.Restart();
        var events = level.DeserializeEvents();
        sw.Stop();
        Report("反序列化事件", sw);
        TestContext.Out.WriteLine($"  events = {events.Count:N0}");

        sw.Restart();
        var floors = level.CreateFloors(usePositionTrack: true);
        sw.Stop();
        Report("生成砖块", sw);
        TestContext.Out.WriteLine($"  floors = {floors.Count:N0}");

        sw.Restart();
        long verts = 0, tris = 0;
        foreach (var f in floors)
        {
            var baked = new SharpFAI.Player.Engine.FloorMesh(
                f.entryAngle, f.exitAngle, Floor.width / 100f, Floor.length / 100f,
                f.isMidspin ? 3 : 40).Build();
            verts += baked.Vertices.Length;
            tris += baked.Indices.Length;
        }
        sw.Stop();
        Report("生成多边形", sw);
        TestContext.Out.WriteLine($"  verts = {verts:N0}, tris = {tris:N0}");

        total.Stop();
        Report("总计", total);
    }
}
