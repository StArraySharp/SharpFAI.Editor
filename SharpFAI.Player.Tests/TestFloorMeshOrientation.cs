using NUnit.Framework;
using SharpFAI.Player.Engine;
using System.Linq;

namespace SharpFAI.Player.Tests;

/// <summary>
/// 确认 FloorMesh 在不同 entry/exit 角下生成砖块的朝向，
/// 用来对齐官方 SetAngle 的角度基准。
/// Report the orientation FloorMesh produces for several entry/exit angles, so the angle base can
/// be matched against the game's SetAngle.
/// </summary>
[TestFixture]
public class TestFloorMeshOrientation
{
    [TestCase(0.0, 0.0)]
    [TestCase(90.0, 90.0)]
    [TestCase(180.0, 180.0)]
    [TestCase(-90.0, -90.0)]
    [TestCase(0.0, 90.0)]
    public void ReportBounds(double entry, double exit)
    {
        FloorMesh mesh = new(entry, exit, 0.45f, 0.78f, 40, scale: 1f);
        FloorMeshResult result = mesh.Build();

        // 只看主体顶点：阴影带会把包围盒撑成近正方形，掩盖真实朝向
        // Only body vertices: the shadow band inflates the bounding box into a near square and
        // hides the real orientation
        var body = result.Vertices
            .Where((_, i) => i < result.Uv0.Length && (float)result.Uv0[i].Y <= 1.0f)
            .ToArray();

        float minX = body.Min(v => v.X);
        float maxX = body.Max(v => v.X);
        float minY = body.Min(v => v.Y);
        float maxY = body.Max(v => v.Y);

        float width = maxX - minX;
        float height = maxY - minY;
        TestContext.Out.WriteLine(
            $"({entry},{exit}) verts={result.Vertices.Length} tris={result.Indices.Length / 3} " +
            $"body w={width:0.00} h={height:0.00} x[{minX:0.00},{maxX:0.00}] y[{minY:0.00},{maxY:0.00}]");
    }
}
