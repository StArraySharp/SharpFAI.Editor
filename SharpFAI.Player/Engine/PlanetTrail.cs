using OpenTK.Mathematics;
using Thrust;

namespace SharpFAI.Player.Engine;

/// <summary>
/// Planet trail: tracks each planet's own position history, resamples it by arc length into
/// particles and draws them. Because it samples a **single planet's continuous path** rather than a
/// role, it is immune to frame rate and speed and can never break apart.
/// </summary>
public sealed class PlanetTrail
{
    /// <summary>Draw data for one trail particle.</summary>
    public readonly struct Sample
    {
        public readonly Vector2 Center;
        public readonly float Size;
        public readonly Vector4 Tint;

        public Sample(Vector2 center, float size, Vector4 tint)
        {
            Center = center;
            Size = size;
            Tint = tint;
        }
    }

    // Trail parameters ----

    /// <summary>Particle lifetime in seconds.</summary>
    public const double Lifetime = 0.94;

    /// <summary>Initial particle size.</summary>
    public const float StartSize = 0.8f;

    /// <summary>ColourModule start colour, red.</summary>
    public static readonly Vector4 RedTint = new(1f, 0f, 0f, 0.5333f);

    /// <summary>ColourModule start colour, blue.</summary>
    public static readonly Vector4 BlueTint = new(0f, 0.19f, 1f, 0.5333f);

    /// <summary>Red component at the end of the colour ramp.</summary>
    private const float RampEndRed = 0.6118f;

    /// <summary>Sample spacing in pixels.</summary>
    private const float SpacingArc = 10f;
    private const int MaximumSamplesPerPlanet = 32768;

    /// <summary>World-unit to pixel scale.</summary>
    private const float WorldScale = 100f;

    /// <summary>History buffer capacity.</summary>
    private const int Capacity = 4096;

    /// <summary>Threshold below which a move counts as "no move".</summary>
    private const float DuplicateEpsilonSquared = 0.25f;

    // One history per planet: position, timestamp and the colour in effect at that moment
    private readonly Vector2[] _positionsRed = new Vector2[Capacity];
    private readonly Vector2[] _positionsBlue = new Vector2[Capacity];
    private readonly double[] _timesRed = new double[Capacity];
    private readonly double[] _timesBlue = new double[Capacity];
    private readonly Vector4[] _tintsRed = new Vector4[Capacity];
    private readonly Vector4[] _tintsBlue = new Vector4[Capacity];
    private readonly Vector2[] _positionsThird = new Vector2[Capacity];
    private readonly double[] _timesThird = new double[Capacity];
    private readonly Vector4[] _tintsThird = new Vector4[Capacity];

    private int _headRed;
    private int _headBlue;
    private int _countRed;
    private int _countBlue;
    private int _headThird;
    private int _countThird;

    // Reused sample buffer so drawing allocates nothing per frame
    private Sample[] _samples = new Sample[1024];

    /// <summary>Number of samples produced last frame.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Clear all history.</summary>
    public void Clear()
    {
        _headRed = 0;
        _headBlue = 0;
        _countRed = 0;
        _countBlue = 0;
        _headThird = 0;
        _countThird = 0;
        SampleCount = 0;
    }

    /// <summary>
    /// Record both planets' current positions. The primary rests on its tile and the secondary rides
    /// the arc -- both are continuous, and when they trade places their positions coincide, so each
    /// planet's own path stays unbroken.
    /// </summary>
    /// <param name="primary">Primary planet position.</param>
    /// <param name="secondary">Secondary planet position.</param>
    /// <param name="primaryTint">Primary trail colour.</param>
    /// <param name="secondaryTint">Secondary trail colour.</param>
    /// <param name="now">Current playback time in seconds.</param>
    public void Record(Vector2 primary, Vector2 secondary, bool swapped, Vector4 primaryTint, Vector4 secondaryTint, double now)
    {
        RecordPlanet(swapped ? 1 : 0, primary, primaryTint, now);
        RecordPlanet(swapped ? 0 : 1, secondary, secondaryTint, now);
    }

    internal void RecordPlanet(int id, Vector2 position, Vector4 tint, double now)
    {
        switch (id)
        {
            case 0:
                Push(_positionsBlue, _timesBlue, _tintsBlue, ref _headBlue, ref _countBlue, position, tint, now);
                break;
            case 1:
                Push(_positionsRed, _timesRed, _tintsRed, ref _headRed, ref _countRed, position, tint, now);
                break;
            default:
                Push(_positionsThird, _timesThird, _tintsThird, ref _headThird, ref _countThird, position, tint, now);
                break;
        }
    }

    internal void ResetPlanet(int id)
    {
        switch (id)
        {
            case 0: _countBlue = 0; break;
            case 1: _countRed = 0; break;
            case 2: _countThird = 0; break;
        }
    }

    /// <summary>
    /// Resample the histories by arc length into drawable particles. The returned span stays valid
    /// until the next call.
    /// </summary>
    public ReadOnlySpan<Sample> Build(double now)
    {
        // Expire every history, including planets that are no longer being recorded.
        Trim(_timesRed, _headRed, now, ref _countRed);
        Trim(_timesBlue, _headBlue, now, ref _countBlue);
        Trim(_timesThird, _headThird, now, ref _countThird);
        int needed = EstimateCount(_positionsRed, _headRed, _countRed)
                   + EstimateCount(_positionsBlue, _headBlue, _countBlue)
                   + EstimateCount(_positionsThird, _headThird, _countThird);

        if (needed > _samples.Length)
        {
            _samples = new Sample[Math.Max(needed, _samples.Length * 2)];
        }

        int written = 0;
        written += Resample(_positionsRed, _timesRed, _tintsRed, _headRed, _countRed, now, _samples, written);
        written += Resample(_positionsBlue, _timesBlue, _tintsBlue, _headBlue, _countBlue, now, _samples, written);
        written += Resample(_positionsThird, _timesThird, _tintsThird, _headThird, _countThird, now, _samples, written);

        SampleCount = written;
        return _samples.AsSpan(0, written);
    }

    /// <summary>Estimate how many samples a history yields.</summary>
    private static int EstimateCount(Vector2[] positions, int head, int count)
    {
        if (count < 2)
        {
            return 0;
        }

        float length = 0f;

        for (int i = 1; i < count; i++)
        {
            int a = Index(head, count, i - 1);
            int b = Index(head, count, i);
            length += Vector2.Distance(positions[a], positions[b]);
        }

        return (int)MathF.Min(length / SpacingArc + 2f, MaximumSamplesPerPlanet);
    }

    /// <summary>
    /// Resample one history at equal arc-length intervals. Positions interpolate between neighbouring
    /// history points; the spacing is small enough that the polyline error is invisible.
    /// </summary>
    private static int Resample(
        Vector2[] positions, double[] times, Vector4[] tints,
        int head, int count, double now, Sample[] output, int offset)
    {
        if (count < 2)
        {
            return 0;
        }

        // Total arc length
        float totalLength = 0f;

        for (int i = 1; i < count; i++)
        {
            totalLength += Vector2.Distance(
                positions[Index(head, count, i - 1)],
                positions[Index(head, count, i)]);
        }

        if (totalLength <= 1e-4f)
        {
            return 0;
        }

        int points = (int)Math.Clamp(totalLength / SpacingArc + 1f, 2f, MaximumSamplesPerPlanet);
        float step = totalLength / (points - 1);

        int segment = 0;
        float segmentStart = 0f;
        float segmentLength = Vector2.Distance(
            positions[Index(head, count, 0)],
            positions[Index(head, count, 1)]);

        int written = 0;

        double age = now - times[Index(head, count, count - 1)];
        float activity = (float)Math.Clamp(1.0 - age / Lifetime, 0.0, 1.0) * MathF.Min(1f, totalLength / SpacingArc);
        for (int i = 0; i < points; i++)
        {
            float target = i * step;

            while (segment < count - 2 && segmentStart + segmentLength < target)
            {
                segmentStart += segmentLength;
                segment++;
                segmentLength = Vector2.Distance(
                    positions[Index(head, count, segment)],
                    positions[Index(head, count, segment + 1)]);
            }

            int a = Index(head, count, segment);
            int b = Index(head, count, segment + 1);

            float local = segmentLength > 1e-4f
                ? Math.Clamp((target - segmentStart) / segmentLength, 0f, 1f)
                : 0f;

            Vector2 position = Vector2.Lerp(positions[a], positions[b], local);
            Vector4 baseTint = tints[a];

            // t = 0 is the oldest sample and t = 1 hugs the planet; the 1.6 power accelerates the fade
            float t = i / (float)(points - 1);
            float fade = MathF.Pow(t, 1.8f);
            float size = fade * StartSize * WorldScale;

            Vector4 tint = new(
                float.Lerp(baseTint.X, RampEndRed, 1f - t),
                float.Lerp(baseTint.Y, 0f, 1f - t),
                float.Lerp(baseTint.Z, 0f, 1f - t),
                baseTint.W * fade * activity);

            output[offset + written] = new Sample(position, size, tint);
            written++;
        }

        return written;
    }

    /// <summary>Map a logical index onto the ring buffer.</summary>
    private static int Index(int head, int count, int index)
    {
        int start = (head - count + Capacity) % Capacity;
        return (start + index) % Capacity;
    }

    /// <summary>Push a point, updating only the timestamp when stationary.</summary>
    private static void Push(
        Vector2[] positions, double[] times, Vector4[] tints,
        ref int head, ref int count, Vector2 position, Vector4 tint, double now)
    {
        if (count > 0)
        {
            int last = (head - 1 + Capacity) % Capacity;

            if (Vector2.DistanceSquared(positions[last], position) < DuplicateEpsilonSquared)
            {
                times[last] = now;
                return;
            }
        }

        positions[head] = position;
        times[head] = now;
        tints[head] = tint;
        head = (head + 1) % Capacity;

        if (count < Capacity)
        {
            count++;
        }
    }

    /// <summary>Drop history points past the lifetime.</summary>
    private static void Trim(double[] times, int head, double now, ref int count)
    {
        while (count > 0)
        {
            int start = (head - count + Capacity) % Capacity;

            if (now - times[start] <= Lifetime)
            {
                break;
            }

            count--;
        }
    }
}
