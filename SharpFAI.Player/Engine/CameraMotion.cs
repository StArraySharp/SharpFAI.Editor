using OpenTK.Mathematics;

namespace SharpFAI.Player.Engine;

internal static class CameraMotion
{
    internal static float PulseScale(double elapsed) => elapsed < 0 || !double.IsFinite(elapsed)
        ? 1f : 5f / (5f - .1f * (float)Math.Clamp(1.0 - elapsed, 0.0, 1.0));

    internal static Vector2 Follow(Vector2 from, Vector2 target, Vector2 holdOffset,
        double elapsed, double bpm)
    {
        double duration = 120.0 / (bpm > 1e-6 ? bpm : 120.0);
        return Vector2.Lerp(from, target + holdOffset, (float)Math.Clamp(elapsed / duration, 0.0, 1.0));
    }

    internal static Vector2 ReleaseHold(Vector2 offset, double elapsed)
    {
        float remaining = 1f - (float)Math.Clamp(elapsed / .3, 0.0, 1.0);
        return offset * (remaining * remaining * remaining);
    }
}
