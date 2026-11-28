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
    internal double ActiveLifetime { get; set; } = Lifetime;
    internal Vector3? FireColorOverride { get; set; }
    internal Vector3? IceColorOverride { get; set; }

    /// <summary>Initial particle size.</summary>
    public const float StartSize = 0.8f;

    /// <summary>ColourModule start colour, red.</summary>
    public static readonly Vector4 RedTint = new(1f, 0f, 0f, 0.5333f);

    /// <summary>ColourModule start colour, blue.</summary>
    public static readonly Vector4 BlueTint = new(0f, 0.19f, 1f, 0.5333f);

    /// <summary>Brightness at the oldest end of the colour ramp.</summary>
    private const float RampEndIntensity = 0.6118f;

    /// <summary>Sample spacing in pixels.</summary>
    private const float SpacingArc = 10f;
    private const int MaximumSamplesPerPlanet = 32768;
    private static readonly Lazy<float[]> MaximumDensityFade = new(() =>
    {
        var values = new float[MaximumSamplesPerPlanet];
        for (int i = 0; i < values.Length; i++)
            values[i] = MathF.Pow(i / (float)(values.Length - 1), 1.8f);
        return values;
    });

    /// <summary>World-unit to pixel scale.</summary>
    private const float WorldScale = 100f;

    /// <summary>History buffer capacity.</summary>
    private const int Capacity = 4096;
    private const int MaximumHistoryCapacity = 65536;

    /// <summary>Threshold below which a move counts as "no move".</summary>
    private const float DuplicateEpsilonSquared = 0.25f;

    private readonly History _blue = new(Capacity);
    private readonly History _red = new(Capacity);
    private readonly History _third = new(Capacity);
    private Sample[] _samples = new Sample[1024];

    /// <summary>Number of samples produced last frame.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Clear all history.</summary>
    public void Clear()
    {
        _red.Clear();
        _blue.Clear();
        _third.Clear();
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

    internal void RecordPlanet(int id, Vector2 position, Vector4 tint, double now, bool preserveHistory = false)
    {
        if (ActiveLifetime <= 0)
            return;

        Track(id).Record(position, tint, now, ActiveLifetime, preserveHistory);
    }

    internal void ResetPlanet(int id)
    {
        if (id is >= 0 and <= 2)
            Track(id).Clear();
    }

    private History Track(int id) => id switch { 0 => _blue, 1 => _red, _ => _third };

    /// <summary>
    /// Resample the histories by arc length into drawable particles. The returned span stays valid
    /// until the next call.
    /// </summary>
    public ReadOnlySpan<Sample> Build(double now) => Build(now, null);

    internal ReadOnlySpan<Sample> Build(double now, FloorRegion? view)
    {
        if (ActiveLifetime <= 0)
        {
            SampleCount = 0;
            return [];
        }

        _red.Trim(now, ActiveLifetime);
        _blue.Trim(now, ActiveLifetime);
        _third.Trim(now, ActiveLifetime);
        EnsureSampleCapacity(ref _samples, _red.Estimate() + _blue.Estimate() + _third.Estimate());

        int written = _red.Resample(now, ActiveLifetime, _samples, 0, view, FireColorOverride);
        written += _blue.Resample(now, ActiveLifetime, _samples, written, view, IceColorOverride);
        written += _third.Resample(now, ActiveLifetime, _samples, written, view, null);
        SampleCount = written;
        return _samples.AsSpan(0, written);
    }

    private static void EnsureSampleCapacity(ref Sample[] samples, int needed)
    {
        if (needed > samples.Length)
            samples = new Sample[Math.Max(needed, samples.Length * 2)];
    }

    internal sealed class History(int capacity)
    {
        private Vector2[] _positions = [];
        private double[] _times = [];
        private Vector4[] _tints = [];
        private int _head, _count;

        internal void Clear() => _count = 0;

        internal void Record(Vector2 position, Vector4 tint, double now, double lifetime, bool preserveHistory)
        {
            if (_positions.Length == 0)
            {
                _positions = new Vector2[capacity];
                _times = new double[capacity];
                _tints = new Vector4[capacity];
            }
            Push(ref _positions, ref _times, ref _tints, ref _head, ref _count, position, tint, now, lifetime, preserveHistory);
        }

        internal void Trim(double now, double lifetime) =>
            PlanetTrail.Trim(_times, _head, now, lifetime, ref _count);

        internal int Estimate() => EstimateCount(_positions, _head, _count);

        internal int Resample(double now, double lifetime, Sample[] output, int offset,
            FloorRegion? view, Vector3? colorOverride) =>
            PlanetTrail.Resample(_positions, _times, _tints, _head, _count, now,
                lifetime, output, offset, view, colorOverride);

        internal ReadOnlySpan<Sample> Build(double now, double lifetime, Vector3? colorOverride, ref Sample[] output)
        {
            Trim(now, lifetime);
            EnsureSampleCapacity(ref output, Estimate());
            return output.AsSpan(0, Resample(now, lifetime, output, 0, null, colorOverride));
        }
    }

    /// <summary>Estimate how many samples a history yields.</summary>
    private static int EstimateCount(Vector2[] positions, int head, int count)
    {
        if (count < 2)
        {
            return 0;
        }

        float length = 0f;
        int mask = positions.Length - 1;

        for (int i = 1; i < count; i++)
        {
            int a = Index(head, count, i - 1, mask);
            int b = Index(head, count, i, mask);
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
        int head, int count, double now, double lifetime, Sample[] output, int offset, FloorRegion? view,
        Vector3? colorOverride)
    {
        if (count < 2)
        {
            return 0;
        }

        // Total arc length
        float totalLength = 0f;
        int mask = positions.Length - 1;

        for (int i = 1; i < count; i++)
        {
            totalLength += Vector2.Distance(
                positions[Index(head, count, i - 1, mask)],
                positions[Index(head, count, i, mask)]);
        }

        if (totalLength <= 1e-4f)
        {
            return 0;
        }

        int points = (int)Math.Clamp(totalLength / SpacingArc + 1f, 2f, MaximumSamplesPerPlanet);
        float[]? fades = points == MaximumSamplesPerPlanet ? MaximumDensityFade.Value : null;
        float step = totalLength / (points - 1);

        int segment = 0;
        float segmentStart = 0f;
        float segmentLength = Vector2.Distance(
            positions[Index(head, count, 0, mask)],
            positions[Index(head, count, 1, mask)]);

        int written = 0;

        double age = now - times[Index(head, count, count - 1, mask)];
        float activity = (float)Math.Clamp(1.0 - age / lifetime, 0.0, 1.0) * MathF.Min(1f, totalLength / SpacingArc);
        // Use the current palette for the whole trail so changing it does not discard history.
        Vector3 endColor = (colorOverride ?? Vector3.UnitX) * RampEndIntensity;
        for (int i = 0; i < points; i++)
        {
            float target = i * step;

            while (segment < count - 2 && segmentStart + segmentLength < target)
            {
                segmentStart += segmentLength;
                segment++;
                segmentLength = Vector2.Distance(
                    positions[Index(head, count, segment, mask)],
                    positions[Index(head, count, segment + 1, mask)]);
            }

            int a = Index(head, count, segment, mask);
            int b = Index(head, count, segment + 1, mask);

            float local = segmentLength > 1e-4f
                ? Math.Clamp((target - segmentStart) / segmentLength, 0f, 1f)
                : 0f;

            Vector2 position = Vector2.Lerp(positions[a], positions[b], local);
            // The oldest sample has t = 0 and the one nearest the planet has t = 1.
            float t = i / (float)(points - 1);
            float fade = fades?[i] ?? MathF.Pow(t, 1.8f);
            float size = fade * StartSize * WorldScale;
            Vector2 halfSize = new(size * .5f);
            if (view is { } bounds && !bounds.Intersects(new FloorRegion(position - halfSize, position + halfSize)))
                continue;

            Vector4 baseTint = tints[a];
            Vector3 color = colorOverride ?? baseTint.Xyz;
            Vector4 tint = new(
                float.Lerp(color.X, endColor.X, 1f - t),
                float.Lerp(color.Y, endColor.Y, 1f - t),
                float.Lerp(color.Z, endColor.Z, 1f - t),
                baseTint.W * fade * activity);

            output[offset + written] = new Sample(position, size, tint);
            written++;
        }

        return written;
    }

    /// <summary>Map a logical index onto the ring buffer.</summary>
    private static int Index(int head, int count, int index, int mask) =>
        (head - count + index) & mask;

    /// <summary>Push a point, updating only the timestamp when stationary.</summary>
    private static void Push(
        ref Vector2[] positions, ref double[] times, ref Vector4[] tints,
        ref int head, ref int count, Vector2 position, Vector4 tint, double now, double lifetime, bool preserveHistory)
    {
        if (count > 0)
        {
            int last = (head - 1) & (positions.Length - 1);

            if (Vector2.DistanceSquared(positions[last], position) < DuplicateEpsilonSquared)
            {
                times[last] = now;
                return;
            }
        }

        if (preserveHistory && count == positions.Length)
        {
            Trim(times, head, now, lifetime, ref count);
            if (count == positions.Length && positions.Length < MaximumHistoryCapacity)
            {
                // Sub-frame sampling can fill the ring before any point expires.
                Vector2[] nextPositions = GrowRing(positions, head);
                double[] nextTimes = GrowRing(times, head);
                Vector4[] nextTints = GrowRing(tints, head);
                positions = nextPositions;
                times = nextTimes;
                tints = nextTints;
                head = count;
            }
        }

        positions[head] = position;
        times[head] = now;
        tints[head] = tint;
        head = (head + 1) & (positions.Length - 1);

        if (count < positions.Length)
        {
            count++;
        }
    }

    private static T[] GrowRing<T>(T[] data, int head)
    {
        T[] larger = new T[data.Length * 2];
        data.AsSpan(head).CopyTo(larger);
        data.AsSpan(0, head).CopyTo(larger.AsSpan(data.Length - head));
        return larger;
    }

    /// <summary>Drop history points past the lifetime.</summary>
    private static void Trim(double[] times, int head, double now, double lifetime, ref int count)
    {
        while (count > 0)
        {
            int start = (head - count) & (times.Length - 1);

            if (now - times[start] <= lifetime)
            {
                break;
            }

            count--;
        }
    }
}
