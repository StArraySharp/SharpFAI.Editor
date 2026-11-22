using NUnit.Framework;
using SharpFAI.Player.Engine;
using System.Collections.Generic;
using System.Linq;

namespace SharpFAI.Player.Tests;

/// <summary>
/// 验证阴影三角形是否真的把内缘（uv.y=1）和外缘（uv.y=2）配在一起。
/// 只有跨这两个档位的三角形，光栅化插值才会产生 1..2 的渐变；
/// 若三角形两端都在 uv.y=2，alpha 恒为 0，阴影完全不可见。
/// Verify that shadow triangles really pair the inner rim (uv.y = 1) with the outer rim
/// (uv.y = 2). Only triangles spanning both levels interpolate a 1..2 ramp; a triangle with both
/// ends at uv.y = 2 yields alpha 0 and the shadow disappears.
/// </summary>
[TestFixture]
public class TestShadowTriangles
{
    [TestCase(0.0, 90.0, "right")]
    [TestCase(0.0, 0.0, "straight")]
    [TestCase(0.0, 45.0, "bend45")]
    public void ShadowTrianglesSpanBothRims(double entry, double exit, string name)
    {
        FloorMesh mesh = new(entry, exit);
        FloorMeshResult result = mesh.Build();

        int[] indices = result.Indices;
        int spanning = 0;
        int allOuter = 0;
        int allInner = 0;

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            float a = (float)result.Uv0[indices[t]].Y;
            float b = (float)result.Uv0[indices[t + 1]].Y;
            float c = (float)result.Uv0[indices[t + 2]].Y;

            int outer = new[] { a, b, c }.Count(v => v > 1.5f);
            int inner = new[] { a, b, c }.Count(v => v > 0.9f && v < 1.5f);

            if (outer > 0 && inner > 0)
            {
                spanning++;
            }
            else if (outer == 3)
            {
                allOuter++;
            }
            else if (inner == 3)
            {
                allInner++;
            }
        }

        TestContext.Out.WriteLine(
            $"{name}: spanning={spanning} allOuter={allOuter} allInner={allInner}");

        Assert.That(spanning, Is.GreaterThan(0),
            $"{name} 没有跨内外缘的阴影三角形 / no triangle spans both rims");
    }

    [TestCase(0.0, 90.0, "right")]
    [TestCase(0.0, 0.0, "straight")]
    public void ReportRimCounts(double entry, double exit, string name)
    {
        FloorMesh mesh = new(entry, exit);
        FloorMeshResult result = mesh.Build();

        int outerRim = result.Uv0.Count(uv => (float)uv.Y > 1.5f);
        int innerRim = result.Uv0.Count(uv =>
        {
            float v = (float)uv.Y;
            return v > 0.9f && v < 1.5f;
        });

        TestContext.Out.WriteLine($"{name}: outerRim={outerRim} innerRim={innerRim}");

        Assert.That(outerRim, Is.GreaterThan(0));
        Assert.That(innerRim, Is.GreaterThan(0));
    }
}
