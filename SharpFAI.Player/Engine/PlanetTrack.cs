using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using SharpFAI.Util;

namespace SharpFAI.Player.Engine;

internal readonly record struct PauseAngleCorrection(double Offset, double Duration)
{
    public double At(double elapsed)
    {
        double progress = Math.Clamp(elapsed / Duration, 0.0, 1.0);
        return Offset * (1.0 - Math.Sin(progress * Math.PI / 2.0));
    }
}

internal readonly record struct PauseStartTiming(double ReadyTime, double BeatLength, int LeadBeats)
{
    internal double StartTime(double minimumTime, bool manual) =>
        Math.Max(minimumTime, ReadyTime - (manual ? LeadBeats * BeatLength : 0));
}

internal readonly record struct PlanetTrackFloor(
    float Radius, int Count, int Pivot, int Orbit, int Third,
    int HoldLength, float HoldDistance, bool LandingAnimation)
{
    public bool HasHold => HoldLength >= 0;

    public static PlanetTrackFloor[] Resolve(IReadOnlyList<FloorTimingCalculator.FloorData> timing,
        IReadOnlyList<PlaybackEvent> events)
    {
        ILookup<int, PlaybackEvent> byFloor = events
            .Where(item => item.IsActive && item is ScaleRadius or HoldTrack)
            .ToLookup(item => item.Floor);
        PlanetTrackFloor[] result = new PlanetTrackFloor[timing.Count];
        float radius = 1f;
        int pivot = 0, orbit = 1, third = 2;
        for (int i = 0; i < result.Length; i++)
        {
            FloorTimingCalculator.FloorData data = timing[i];
            int count = data.NumPlanets;
            if (i > 0)
            {
                // A midspin and its successor can share an entry time. Resolve both handoffs.
                if (count == 3)
                    (pivot, orbit, third) = data.MidSpin && result[i - 1].Count == 3
                        ? (third, pivot, orbit) : (orbit, third, pivot);
                else
                    (pivot, orbit) = (orbit, pivot);
            }

            float holdDistance = 1f;
            bool landing = false;
            foreach (PlaybackEvent item in byFloor[i])
            {
                if (item is ScaleRadius scale && scale.Enabled("scale", scale.Scale) is float percent && float.IsFinite(percent))
                {
                    radius = MathF.Max(0f, percent / 100f);
                }
                else if (item is HoldTrack hold)
                {
                    holdDistance = hold.Enabled("distanceMultiplier", hold.DistanceMultiplier) is float distance
                        && float.IsFinite(distance) ? MathF.Max(0f, distance / 100f) : 1f;
                    landing = hold.Enabled("landingAnimation", hold.LandingAnimation) == true;
                }
            }

            double turn = Math.Abs(Math.IEEERemainder(data.ExitAngle - data.EntryAngle, Math.Tau));
            float extension = data.HoldLength >= 0 && turn > 1e-5
                ? (2f * data.HoldLength + 1f) * holdDistance : 0f;
            result[i] = new PlanetTrackFloor(radius, count, pivot, orbit, third, data.HoldLength, extension, landing);
        }

        return result;
    }

    public static void ApplySpacing(IReadOnlyList<Floor> floors, ReadOnlySpan<PlanetTrackFloor> track, float spacing)
    {
        System.Numerics.Vector2 offset = default;
        for (int i = 1; i < floors.Count; i++)
        {
            float extra = (track[i].Radius - 1f + track[i - 1].HoldDistance) * spacing;
            if (extra != 0f)
            {
                Vector2 along = Direction(floors[i - 1].angle);
                offset += new System.Numerics.Vector2(along.X, along.Y) * extra;
            }
            floors[i].position += offset;
        }
    }

    public static Vector2 Direction(double degrees)
    {
        (float sin, float cos) = MathF.SinCos((float)(degrees * Math.PI / 180.0));
        return new Vector2(cos, sin);
    }

    public static Vector2 HoldPivot(Vector2 start, Vector2 landing, double exit, float landingRadius, float progress) =>
        Vector2.Lerp(start, landing - Direction(exit) * landingRadius, progress);

    public static float ThirdAngle(bool clockwise) => clockwise ? 60f : -60f;
}
