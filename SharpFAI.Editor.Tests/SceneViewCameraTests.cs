using NUnit.Framework;
using OpenTK.Mathematics;

namespace SharpFAI.Editor.Tests;

public sealed class SceneViewCameraTests
{
    [Test]
    public void AxisViewsCanFlipAndPerspectiveScalesNearerPoints()
    {
        var camera = new SceneViewCamera();
        camera.SnapAxis(2);
        var top = camera.CreateProjection(new Vector2(800, 600), 150);
        Assert.That(top.Normal.Z, Is.EqualTo(1).Within(1e-5));
        Assert.That(top.Orthographic, Is.True);
        Assert.That(top.Project(new Vector3(60, 0, 150)).X, Is.EqualTo(top.Project(new Vector3(60, 0, 0)).X));

        camera.Orthographic = false;
        var perspective = camera.CreateProjection(new Vector2(800, 600), 150);
        Assert.That(perspective.Project(new Vector3(60, 0, 150)).X, Is.GreaterThan(perspective.Project(new Vector3(60, 0, 0)).X));
        camera.SnapAxis(2);
        Assert.That(camera.CreateProjection(Vector2.One, 150).Normal.Z, Is.EqualTo(-1).Within(1e-5));
    }

    [Test]
    public void NearPlaneClipsFacesAndPanRetainsPrecisionAtDistantCoordinates()
    {
        var camera = new SceneViewCamera();
        camera.Focus(new Vector3d(1e9, 1e9, 0), resetDistance: true);
        camera.Pan(new Vector2(1, 0), 600, 150);
        Assert.That(camera.Pivot.X, Is.LessThan(1e9));

        camera.SnapAxis(2);
        camera.Orthographic = false;
        var view = camera.CreateProjection(new Vector2(800, 600), 150);
        Vector3[] face = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, view.Distance * 2), new(-1, 1, 0)];
        Span<Vector2> result = stackalloc Vector2[8];
        int count = view.ClipPolygon(face, result);
        Assert.That(count, Is.EqualTo(5));
        foreach (var point in result[..count])
            Assert.That(float.IsFinite(point.X) && float.IsFinite(point.Y), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RenderProjectionMatchesEditorOverlaysAndIncludesVisibleGround(bool orthographic)
    {
        var camera = new SceneViewCamera { Orthographic = orthographic };
        camera.Focus(new Vector3d(1234.125, 789.5, 115), resetDistance: false);
        var size = new Vector2(960, 480);
        const float tile = 150;
        var view = camera.CreateProjection(size, tile);
        var renderView = camera.CreateRenderView(size, tile);
        var world = new Vector3(1420, 845, 0);
        var clip = Vector4.TransformRow(new Vector4(world.X - renderView.Origin.X, world.Y - renderView.Origin.Y, world.Z, 1),
            renderView.RelativeProjection);
        var screen = (new Vector2(clip.X, -clip.Y) / clip.W + Vector2.One) * size * .5f;
        var overlay = view.Project((Vector3)((Vector3d)world - camera.Pivot));
        Assert.That(screen.X, Is.EqualTo(overlay.X).Within(.001));
        Assert.That(screen.Y, Is.EqualTo(overlay.Y).Within(.001));
        Assert.That(renderView.Coverage.Min.X, Is.LessThan(world.X));
        Assert.That(renderView.Coverage.Max.X, Is.GreaterThan(world.X));
        Assert.That(renderView.Coverage.Min.Y, Is.LessThan(world.Y));
        Assert.That(renderView.Coverage.Max.Y, Is.GreaterThan(world.Y));
    }
}
