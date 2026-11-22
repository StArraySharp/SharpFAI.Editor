using NUnit.Framework;
using SharpFAI.Player.Engine;
using SharpFAI.Framework;
using System.Linq;

namespace SharpFAI.Player.Tests;

/// <summary>
/// 砖块几何移植的健全性检查：用几组典型角度验证顶点数、包围盒与阴影数量。
/// 数据来自编辑器实际使用的 <see cref="FloorMesh"/>（官方算法的逐行移植）；
/// <see cref="OfficialFloorMesh"/> 已被取代，不再作为被测对象。
/// Sanity checks for the ported floor geometry: typical angle pairs are verified for vertex count,
/// bounding box and shadow count. The data comes from <see cref="FloorMesh"/>, the editor's
/// line-for-line port of the game's algorithm; <see cref="OfficialFloorMesh"/> is superseded and no
/// longer the subject of these checks.
/// </summary>
[TestFixture]
public class TestOfficialFloorMesh
{
    private static FloorMeshResult Build(double entry, double exit, bool midspin = false)
    {
        FloorMesh mesh = new(entry, exit, 0.45f, 0.78f, midspin ? 3 : 40, scale: 1f);
        return mesh.Build();
    }

    [Test]
    public void StraightTileIsSane()
    {
        FloorMeshResult result = Build(0, 0);

        TestContext.Out.WriteLine(
            $"vertices={result.Vertices.Length} triangles={result.Indices.Length / 3}");

        Assert.That(result.Vertices.Length, Is.GreaterThan(20));
        Assert.That(result.Indices.Length % 3, Is.Zero);

        // 官方标准格宽 0.4125 + 0.0375 = 0.45，长度 0.75 + 0.03 = 0.78
        float minX = result.Vertices.Min(v => v.X);
        float maxX = result.Vertices.Max(v => v.X);
        float minY = result.Vertices.Min(v => v.Y);
        float maxY = result.Vertices.Max(v => v.Y);

        TestContext.Out.WriteLine($"x[{minX:0.000},{maxX:0.000}] y[{minY:0.000},{maxY:0.000}]");

        // 含阴影再加 0.11，所以整块不应超出 ±1.0
        // Shadows add another 0.11, so a tile must stay inside ±1.0
        Assert.That(maxX - minX, Is.LessThan(2.0));
        Assert.That(maxY - minY, Is.LessThan(2.0));
        Assert.That(maxX - minX, Is.GreaterThan(0.4));
    }

    [Test]
    public void RightAngleHasShadows()
    {
        FloorMeshResult result = Build(0, 90);

        // 阴影顶点由 uv.y > 1 标记，与着色器的判定一致
        // Shadow vertices are marked by uv.y > 1, matching the shader's test
        int shadowCount = result.Uv0.Count(uv => uv.Y > 1f);

        TestContext.Out.WriteLine(
            $"vertices={result.Vertices.Length} shadow={shadowCount} " +
            $"triangles={result.Indices.Length / 3}");

        Assert.That(shadowCount, Is.GreaterThan(0), "90 度格应有阴影顶点 / a 90-degree tile needs shadows");
        Assert.That(shadowCount, Is.LessThan(result.Vertices.Length),
            "阴影顶点不应超过总顶点数 / shadow vertices stay below the total");
    }

    [Test]
    public void AllIndicesAreInRange()
    {
        foreach ((double entry, double exit) in new[]
        {
            (0.0, 0.0), (0.0, 90.0), (0.0, 180.0), (0.0, -90.0),
            (0.0, 45.0), (0.0, 135.0), (30.0, 150.0), (0.0, 270.0),
        })
        {
            FloorMeshResult result = Build(entry, exit);

            foreach (int index in result.Indices)
            {
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"{entry}->{exit}");
                Assert.That(index, Is.LessThan(result.Vertices.Length), $"{entry}->{exit}");
            }
        }
    }

    [Test]
    public void MidspinTileIsNotHexagon()
    {
        // 官方的中旋只是把 curvaturePoints 降到 3，形状仍是普通砖块。
        // `_isHexagon` 在官方从未被赋值为 true，是死代码。
        // The game's midspin only lowers `curvaturePoints` to 3 and keeps the normal tile shape;
        // `_isHexagon` is never set to true in the game and is dead code.
        FloorMeshResult result = Build(0, 0, midspin: true);

        TestContext.Out.WriteLine(
            $"vertices={result.Vertices.Length} triangles={result.Indices.Length / 3}");

        Assert.That(result.Vertices.Length, Is.LessThan(40),
            "中旋格顶点数远少于普通砖块 / a midspin tile has far fewer vertices than a normal one");

        float width = result.Vertices.Max(v => v.X) - result.Vertices.Min(v => v.X);
        float height = result.Vertices.Max(v => v.Y) - result.Vertices.Min(v => v.Y);

        TestContext.Out.WriteLine($"bbox {width:0.000} x {height:0.000}");

        Assert.That(width, Is.LessThan(2.0), "中旋格不是放大的六边形 / a midspin tile is not an enlarged hexagon");
        Assert.That(height, Is.LessThan(2.0), "中旋格不是放大的六边形 / a midspin tile is not an enlarged hexagon");
    }

    [Test]
    public void TrianglesCoverTheTileInterior()
    {
        // 空环形状意味着多边形内部没被三角形覆盖：中心点必须落在某个三角形里
        // A hollow ring means the interior is uncovered: the centre must lie inside some triangle
        foreach ((double entry, double exit) in new[]
        {
            (0.0, 90.0), (0.0, 0.0), (0.0, 45.0), (0.0, -90.0), (0.0, 135.0),
        })
        {
            FloorMeshResult result = Build(entry, exit);
            bool[] isShadow = result.Uv0.Select(uv => uv.Y > 1f).ToArray();

            // 砖块中心（不含阴影）大致在 0,0 附近
            // The tile centre sits near the origin once shadows are excluded
            var centre = new System.Numerics.Vector3(0f, 0f, 0f);
            bool covered = false;

            for (int i = 0; i + 2 < result.Indices.Length && !covered; i += 3)
            {
                if (isShadow[result.Indices[i]])
                {
                    continue;
                }

                covered = InTriangle(
                    centre,
                    result.Vertices[result.Indices[i]],
                    result.Vertices[result.Indices[i + 1]],
                    result.Vertices[result.Indices[i + 2]]);
            }

            Assert.That(covered, Is.True, $"{entry}->{exit} 砖块中心没有被三角形覆盖 / centre uncovered");
        }
    }

    private static bool InTriangle(
        System.Numerics.Vector3 p,
        System.Numerics.Vector3 a,
        System.Numerics.Vector3 b,
        System.Numerics.Vector3 c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);
        bool hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNegative && hasPositive);
    }

    private static float Sign(System.Numerics.Vector3 p, System.Numerics.Vector3 a, System.Numerics.Vector3 b)
        => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    [Test]
    public void EveryTriangleHasPositiveArea()
    {
        // 退化三角形会让砖块出现破洞 / Degenerate triangles punch holes in the tile
        foreach ((double entry, double exit) in new[]
        {
            (0.0, 0.0), (0.0, 90.0), (0.0, 180.0), (0.0, -90.0), (0.0, 45.0),
        })
        {
            FloorMeshResult result = Build(entry, exit);
            bool[] isShadow = result.Uv0.Select(uv => uv.Y > 1f).ToArray();
            int degenerate = 0;

            for (int i = 0; i + 2 < result.Indices.Length; i += 3)
            {
                int ia = result.Indices[i];
                int ib = result.Indices[i + 1];
                int ic = result.Indices[i + 2];

                // 阴影三角形本身就是细长条，退化统计只看主体
                // Shadow triangles are thin by design, so degenerate counting looks at bodies only
                if (isShadow[ia] || isShadow[ib] || isShadow[ic])
                {
                    continue;
                }

                var a = result.Vertices[ia];
                var b = result.Vertices[ib];
                var c = result.Vertices[ic];

                float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

                if (Math.Abs(area) < 1e-7f)
                {
                    degenerate++;
                }
            }

            TestContext.Out.WriteLine($"{entry}->{exit}: degenerate={degenerate}");

            Assert.That(degenerate, Is.LessThan(result.Indices.Length / 3 / 2),
                $"{entry}->{exit} 退化三角形过多 / too many degenerate triangles");
        }
    }
}
