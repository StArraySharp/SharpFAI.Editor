using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal static class TrackEase
{
    public static float Evaluate(EventEnums.Ease ease, float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        return ease switch
        {
            EventEnums.Ease.InSine => 1f - MathF.Cos(t * MathF.PI / 2f),
            EventEnums.Ease.OutSine => MathF.Sin(t * MathF.PI / 2f),
            EventEnums.Ease.InOutSine => (1f - MathF.Cos(t * MathF.PI)) / 2f,
            EventEnums.Ease.InQuad => t * t,
            EventEnums.Ease.OutQuad => 1f - (1f - t) * (1f - t),
            EventEnums.Ease.InOutQuad => PowerInOut(t, 2),
            EventEnums.Ease.InCubic => t * t * t,
            EventEnums.Ease.OutCubic => 1f - MathF.Pow(1f - t, 3),
            EventEnums.Ease.InOutCubic => PowerInOut(t, 3),
            EventEnums.Ease.InQuart => MathF.Pow(t, 4),
            EventEnums.Ease.OutQuart => 1f - MathF.Pow(1f - t, 4),
            EventEnums.Ease.InOutQuart => PowerInOut(t, 4),
            EventEnums.Ease.InQuint => MathF.Pow(t, 5),
            EventEnums.Ease.OutQuint => 1f - MathF.Pow(1f - t, 5),
            EventEnums.Ease.InOutQuint => PowerInOut(t, 5),
            EventEnums.Ease.InExpo => Expo(t),
            EventEnums.Ease.OutExpo => 1f - Expo(1f - t),
            EventEnums.Ease.InOutExpo => InOut(t, Expo),
            EventEnums.Ease.InCirc => Circ(t),
            EventEnums.Ease.OutCirc => 1f - Circ(1f - t),
            EventEnums.Ease.InOutCirc => InOut(t, Circ),
            EventEnums.Ease.InBack => Back(t),
            EventEnums.Ease.OutBack => 1f - Back(1f - t),
            EventEnums.Ease.InOutBack => InOut(t, Back),
            EventEnums.Ease.InElastic => Elastic(t),
            EventEnums.Ease.OutElastic => 1f - Elastic(1f - t),
            EventEnums.Ease.InOutElastic => InOut(t, Elastic),
            EventEnums.Ease.InBounce => 1f - Bounce(1f - t),
            EventEnums.Ease.OutBounce => Bounce(t),
            EventEnums.Ease.InOutBounce => InOut(t, BounceIn),
            EventEnums.Ease.Flash => Flash(t),
            EventEnums.Ease.InFlash => Flash(t * t),
            EventEnums.Ease.OutFlash => Flash(1f - (1f - t) * (1f - t)),
            EventEnums.Ease.InOutFlash => Flash(PowerInOut(t, 2)),
            EventEnums.Ease.INTERNAL_Zero => 0f,
            _ => t,
        };
    }

    private static float PowerInOut(float t, int power) => t < 0.5f
        ? MathF.Pow(2f * t, power) / 2f
        : 1f - MathF.Pow(2f - 2f * t, power) / 2f;

    private static float InOut(float t, Func<float, float> enter) =>
        t < 0.5f ? enter(2f * t) / 2f : 1f - enter(2f - 2f * t) / 2f;

    private static float Expo(float t) => MathF.Pow(2f, 10f * (t - 1f));
    private static float Circ(float t) => 1f - MathF.Sqrt(1f - t * t);
    private static float Back(float t) => t * t * (2.70158f * t - 1.70158f);
    private static float Elastic(float t) =>
        -Expo(t) * MathF.Sin((10f * t - 10.75f) * (2f * MathF.PI / 3f));
    private static float Flash(float t) => 1f - MathF.Abs(1f - t * 3f % 2f);
    private static float BounceIn(float t) => 1f - Bounce(1f - t);

    private static float Bounce(float t)
    {
        const float divider = 2.75f;
        const float factor = 7.5625f;
        if (t < 1f / divider) return factor * t * t;
        if (t < 2f / divider) return 0.75f + factor * MathF.Pow(t - 1.5f / divider, 2);
        if (t < 2.5f / divider) return 0.9375f + factor * MathF.Pow(t - 2.25f / divider, 2);
        return 0.984375f + factor * MathF.Pow(t - 2.625f / divider, 2);
    }
}
