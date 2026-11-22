using NUnit.Framework;
using SharpFAI.Player.Engine;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharpFAI.Player.Tests;

/// <summary>
/// 诊断适配版 FloorMesh 输出的 uv.y 分布。
/// 阴影要靠 uv.y 落在 (1,2] 才会被着色器识别，全为 2.0 意味着阴影完全透明。
/// Diagnose the uv.y distribution produced by the adapted FloorMesh. Shadows are only picked up
/// when uv.y falls in (1,2]; an all-2.0 distribution means they are fully transparent.
/// </summary>
[TestFixture]
public class TestFloorMeshUv
{
    [TestCase(0.0, 0.0, "straight")]
    [TestCase(0.0, 90.0, "right")]
    [TestCase(0.0, 45.0, "bend45")]
    [TestCase(0.0, 180.0, "uturn")]
    public void ReportUvYDistribution(double entry, double exit, string name)
    {
        FloorMesh mesh = new(entry, exit);
        FloorMeshResult result = mesh.Build();

        int count = result.Uv0.Length;
        Dictionary<float, int> histogram = [];

        for (int i = 0; i < count; i++)
        {
            float v = (float)Math.Round(result.Uv0[i].Y, 3);

            if (!histogram.TryGetValue(v, out int n))
            {
                n = 0;
            }

            histogram[v] = n + 1;
        }

        TestContext.Out.WriteLine($"--- {name} ({entry}->{exit}) verts={count}");

        foreach ((float v, int n) in histogram.OrderBy(kv => kv.Key))
        {
            string tag = v > 1f ? "SHADOW" : "body";
            TestContext.Out.WriteLine($"   uv.y={v:0.000}  count={n,4}  {tag}");
        }

        // A shadow triangle needs a vertex on the inner rim and one on the outer rim; the ramp
        // between them is pure rasteriser interpolation, so only the triangle matters. Looking at
        // the vertex histogram instead would miss this: the inner rim sits at exactly 1.0, which is
        // also the body's v, so a vertex-only test never sees a value strictly between 1 and 2.
        bool hasInner = false;
        bool hasOuter = false;
        int shadowTriangles = 0;

        for (int t = 0; t + 2 < result.Indices.Length; t += 3)
        {
            float va = result.Uv0[result.Indices[t]].Y;
            float vb = result.Uv0[result.Indices[t + 1]].Y;
            float vc = result.Uv0[result.Indices[t + 2]].Y;

            float max = MathF.Max(va, MathF.Max(vb, vc));

            if (max <= 1f)
            {
                continue;
            }

            shadowTriangles++;

            float min = MathF.Min(va, MathF.Min(vb, vc));

            if (min <= 1f)
            {
                hasInner = true;
            }

            if (max >= 1.9f)
            {
                hasOuter = true;
            }
        }

        TestContext.Out.WriteLine($"   shadow triangles = {shadowTriangles}");

        Assert.That(shadowTriangles, Is.GreaterThan(0), $"{name} 没有阴影三角形 / no shadow triangles");
        Assert.That(hasInner, Is.True, $"{name} 阴影缺少内缘 uv.y=1 / shadow has no inner rim");
        Assert.That(hasOuter, Is.True, $"{name} 阴影缺少外缘 uv.y=2 / shadow has no outer rim");
    }
}
