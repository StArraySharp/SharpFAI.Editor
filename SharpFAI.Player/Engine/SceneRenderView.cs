using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

internal readonly record struct SceneRenderView(Matrix4 RelativeProjection, Vector2 Origin,
    FloorRegion Coverage, Vector3 Right, Vector3 Up, float TileSpacing)
{
    public Matrix4 WorldProjection => Matrix4.CreateTranslation(-Origin.X, -Origin.Y, 0) * RelativeProjection;

    public static float LayerHeight(double depth, float tileSpacing) =>
        (float)(-Math.Asinh(depth / 10) * tileSpacing * .55);

    public Matrix4 ProjectDecoration(Matrix4 model, double depth)
    {
        model.Row3.X -= Origin.X;
        model.Row3.Y -= Origin.Y;
        model.Row3.Z += LayerHeight(depth, TileSpacing);
        return model * RelativeProjection;
    }

    // Test homogeneous frustum planes without dividing by W. A primitive crossing the
    // near plane stays visible and is clipped by the GPU.
    public static bool OutsideClip(Matrix4 clip, Vector3 halfSize)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            if (Outside(clip.Column3 - Column(axis)) || Outside(clip.Column3 + Column(axis)))
                return true;
        }
        return false;

        Vector4 Column(int axis) => axis switch { 0 => clip.Column0, 1 => clip.Column1, _ => clip.Column2 };
        bool Outside(Vector4 plane) => plane.W + Vector3.Dot(Vector3.Abs(plane.Xyz), halfSize) < 0;
    }
}
