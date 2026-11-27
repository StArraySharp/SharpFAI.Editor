using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal sealed class ParticleDecorationTimeline(AddParticleDecoration source, int index, float tileSize)
{
    internal readonly record struct Sample(double Birth, Vector2 ShapeOffset, Vector2 Travel, float Size,
        float Rotation, Vector4 Tint, bool World);

    private readonly record struct Settings(float Rate, Vector2 Lifetime, Vector2 Size, Vector2 StartRotation,
        Vector2 Spin, Vector2 VelocityMin, Vector2 VelocityMax, Vector2 SizeOverLife, Vector2 Drag, float Speed,
        float Duration, bool Loop, int Capacity, float Radius, float Arc, bool Rectangle, bool World,
        ParticleGradient Color, ParticleGradient ColorOverLife);

    private readonly record struct Command(double Time, ParticleControlEvent Event, bool Triggered);
    private readonly record struct Run(double Start, double End, Settings Settings, int Burst, int Sequence,
        double Clock);
    private readonly List<Command> _commands = [];
    private readonly List<Run> _runs = [];
    private (double End, double Lifetime)[] _particleHistory = [];
    private readonly uint _seed = (uint)(Read(source.ParticleFields.GetValueOrDefault("randomSeed"), 0) is > 0 and <= int.MaxValue
        ? (int)Read(source.ParticleFields.GetValueOrDefault("randomSeed"), 0) : index + 1);
    private bool _dirty = true;

    internal void Add(ParticleControlEvent control, double time, bool triggered = false)
    {
        _commands.Add(new(time, control, triggered));
        _dirty = true;
    }

    internal void ResetTriggers()
    {
        if (_commands.RemoveAll(command => command.Triggered) > 0)
            _dirty = true;
    }

    internal void Collect(double time, List<Sample> output)
    {
        output.Clear();
        if (!double.IsFinite(time) || time < 0)
            return;
        if (_dirty)
            Rebuild();

        int firstFuture = 0, high = _runs.Count;
        while (firstFuture < high)
        {
            int middle = firstFuture + (high - firstFuture) / 2;
            if (_runs[middle].Start <= time) firstFuture = middle + 1;
            else high = middle;
        }

        int capacity = 0;
        for (int i = firstFuture - 1; i >= 0; i--)
        {
            // Use the same subtraction as the individual lifetime checks, including boundary births.
            var (latestEnd, longestLifetime) = _particleHistory[i];
            if (latestEnd < time - longestLifetime)
                break;
            Run run = _runs[i];
            if (time < run.Start)
                continue;
            if (run.Burst == -1)
                break;
            if (run.Burst == -2)
            {
                capacity = run.Settings.Capacity;
                continue;
            }
            var settings = run.Settings;
            int cap = capacity > 0 ? capacity : settings.Capacity;
            if (output.Count >= cap || settings.Lifetime.X <= 0 && settings.Lifetime.Y <= 0)
                continue;
            double age = Math.Max(settings.Lifetime.X, settings.Lifetime.Y);
            double birthFloor = time - age / Math.Max(settings.Speed, .001f);
            if (run.Burst > 0)
            {
                if (run.Start >= birthFloor)
                    for (int n = 0; n < run.Burst && output.Count < cap; n++)
                        CollectOne(run, n, run.Start);
                continue;
            }
            double rate = settings.Rate * settings.Speed;
            if (rate <= 0 || run.End < birthFloor)
                continue;
            double first = Math.Max(1, Math.Ceiling((Math.Max(run.Start, birthFloor) - run.Clock) * rate));
            double last = Math.Floor((Math.Min(time, Math.BitDecrement(run.End)) - run.Clock) * rate);
            if (!double.IsFinite(last) || last > long.MaxValue / 2d || last < first)
                continue;
            long newest = (long)last;
            long oldest = Math.Max((long)first, newest - Math.Max(256, cap * 4));
            for (long n = newest; n >= oldest && output.Count < cap; n--)
                CollectOne(run, n, run.Clock + n / rate);
        }
        output.Reverse();

        void CollectOne(Run run, long ordinal, double birth)
        {
            Settings settings = run.Settings;
            uint state = unchecked(_seed ^ ((uint)run.Sequence * 0x9e3779b9u) ^ ((uint)ordinal * 0x85ebca6bu));
            float lifetime = Range(settings.Lifetime, ref state);
            float elapsed = (float)(time - birth) * settings.Speed;
            if (lifetime <= 0 || elapsed < 0 || elapsed >= lifetime)
                return;
            float progress = elapsed / lifetime;
            float angle = Next(ref state) * MathHelper.DegreesToRadians(settings.Arc);
            float radial = MathF.Sqrt(Next(ref state)) * settings.Radius;
            // SetScale writes the rectangle's authored scale in Unity units. Only
            // the circle radius and velocity are expressed relative to a track tile.
            Vector2 position = settings.Rectangle
                ? new Vector2(Next(ref state) - .5f, Next(ref state) - .5f) * 100f
                : new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radial * tileSize);
            Vector2 velocity = new(Mix(settings.VelocityMin.X, settings.VelocityMax.X, Next(ref state)),
                Mix(settings.VelocityMin.Y, settings.VelocityMax.Y, Next(ref state)));
            // Circle emits radially at the prefab's 5 Unity units/s; Rectangle emits along Z.
            if (!settings.Rectangle && tileSize > 0)
                velocity += new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (500f / tileSize);
            float drag = Math.Max(0, Mix(settings.Drag.X, settings.Drag.Y, progress * .5f)) * .01f;
            Vector2 travel = velocity * (drag > .0001f ? (1f - MathF.Exp(-drag * elapsed)) / drag : elapsed);
            float size = Range(settings.Size, ref state) * Mix(settings.SizeOverLife.X, settings.SizeOverLife.Y, progress) * .01f;
            float rotation = Range(settings.StartRotation, ref state) +
                MathHelper.RadiansToDegrees(Range(settings.Spin, ref state) * elapsed);
            Vector4 tint = settings.Color.At(0, Next(ref state)) * settings.ColorOverLife.At(progress, Next(ref state));
            output.Add(new(birth, position, travel, size, rotation, tint, settings.World));
        }
    }

    private void Rebuild()
    {
        _dirty = false;
        _runs.Clear();
        Settings settings = Apply(new(10, new(10), new(100), default, default, default, default, new(100),
            default, 1, 5, true, 1000, 1, 360, false, false, ParticleGradient.White, ParticleGradient.White), source.ParticleFields,
            source, source.ParticleColor);
        bool running = Flag(source.ParticleFields.GetValueOrDefault("autoPlay"), true);
        double cursor = 0, clock = 0;
        int sequence = 0;
        foreach (var (time, control, _) in _commands.OrderBy(c => c.Time))
        {
            double at = Math.Max(0, time);
            if (running)
                AddRun(at);
            cursor = at;
            if (control.EventType == EventType.EmitParticle)
            {
                int count = (int)Math.Clamp(Read(Field(control, "count"), 10), 0, settings.Capacity);
                if (count > 0)
                    _runs.Add(new(at, at, settings, count, ++sequence, at));
                continue;
            }
            settings = Apply(settings, control.ParticleFields, control, Field(control, "color"));
            switch ((string?)Field(control, "targetMode"))
            {
                case "Start":
                    if (!running || !settings.Loop && at >= clock + settings.Duration / Math.Max(settings.Speed, .001f))
                        clock = at;
                    running = true;
                    break;
                case "Clear":
                    _runs.Add(new(at, at, settings, -1, ++sequence, at));
                    running = false;
                    break;
                case "Stop":
                    running = false;
                    break;
            }
            _runs.Add(new(at, at, settings, -2, sequence, at));
        }
        if (running)
            AddRun(double.PositiveInfinity);

        _particleHistory = new (double End, double Lifetime)[_runs.Count];
        double latestEnd = double.NegativeInfinity, longestLifetime = 0;
        for (int i = 0; i < _runs.Count; i++)
        {
            Run run = _runs[i];
            if (run.Burst == -1)
            {
                latestEnd = double.NegativeInfinity;
                longestLifetime = 0;
            }
            else if (run.Burst >= 0)
            {
                latestEnd = Math.Max(latestEnd, run.End);
                double lifetime = Math.Max(run.Settings.Lifetime.X, run.Settings.Lifetime.Y);
                longestLifetime = Math.Max(longestLifetime, lifetime / Math.Max(run.Settings.Speed, .001f));
            }
            _particleHistory[i] = (latestEnd, longestLifetime);
        }

        void AddRun(double end)
        {
            if (!settings.Loop)
                end = Math.Min(end, clock + settings.Duration / Math.Max(settings.Speed, .001f));
            if (end > cursor)
                _runs.Add(new(cursor, end, settings, 0, ++sequence, clock));
        }
    }

    private static Settings Apply(Settings previous, IDictionary<string, JToken> fields, PlaybackEvent item, JToken? color)
    {
        JToken? Get(string key) => item.Enabled(key, fields.TryGetValue(key, out var value) ? value : null);
        var velocity = Get("velocity") as JArray;
        return previous with
        {
            Rate = Math.Clamp(Mean(Pair(Get("emissionRate"), new(previous.Rate))), 0, 100_000),
            Lifetime = Pair(Get("particleLifetime"), previous.Lifetime),
            Size = Pair(Get("particleSize"), previous.Size),
            Spin = Pair(Get("rotationOverTime"), previous.Spin),
            StartRotation = Pair(Get("startRotation"), previous.StartRotation),
            VelocityMin = Pair(velocity?.ElementAtOrDefault(0), previous.VelocityMin),
            VelocityMax = Pair(velocity?.ElementAtOrDefault(1), previous.VelocityMax),
            SizeOverLife = Pair(Get("sizeOverLifetime"), previous.SizeOverLife),
            Drag = Pair(Get("velocityLimitOverLifetime"), previous.Drag),
            Speed = Math.Clamp(Read(Get("simulationSpeed"), previous.Speed * 100f) * .01f, 0, 100),
            Duration = Math.Max(0, Read(Get("playDuration"), previous.Duration)),
            Loop = Flag(Get("loop"), previous.Loop),
            Capacity = (int)Math.Clamp(Read(Get("maxParticles"), previous.Capacity), 1, 100_000),
            Radius = Math.Max(0, Read(Get("shapeRadius"), previous.Radius)),
            Arc = Math.Clamp(Read(Get("arc"), previous.Arc), 0, 360),
            Rectangle = Get("shapeType") is { } shape ? (string?)shape == "Rectangle" : previous.Rectangle,
            World = Get("simulationSpace") is { } space ? (string?)space == "World" : previous.World,
            Color = ParticleGradient.Parse(item.Enabled("color", color)) ?? previous.Color,
            ColorOverLife = ParticleGradient.Parse(Get("colorOverLifetime")) ?? previous.ColorOverLife,
        };
    }

    private static JToken? Field(ParticleControlEvent control, string key) =>
        control.Enabled(key, control.ParticleFields.TryGetValue(key, out var value) ? value : null);

    private static float Read(JToken? token, float fallback) => token?.Type is JTokenType.Float or JTokenType.Integer &&
        (float?)token is { } value && float.IsFinite(value) ? value : fallback;
    private static bool Flag(JToken? token, bool fallback) => token?.Type == JTokenType.Boolean ? (bool)token : fallback;
    private static Vector2 Pair(JToken? token, Vector2 fallback) => token is JArray array
        ? new(Read(array.ElementAtOrDefault(0), fallback.X), Read(array.ElementAtOrDefault(1), fallback.Y))
        : new(Read(token, fallback.X), Read(token, fallback.Y));
    private readonly record struct ParticleGradient(Vector4 Color1, Vector4 Color2,
        GradientCurve? First = null, GradientCurve? Second = null, bool RandomTime = false)
    {
        internal static ParticleGradient White => new(Vector4.One, Vector4.One);

        internal Vector4 At(float time, float variation)
        {
            Vector4 first = First?.At(RandomTime ? variation : time) ?? Color1;
            if (First is not null && Second is null)
                return first;
            return Vector4.Lerp(first, Second?.At(time) ?? Color2, variation);
        }

        internal static ParticleGradient? Parse(JToken? token)
        {
            if (token is not JObject data)
                return ParseColor(token) is { } color ? new(color, color) : null;

            var first = ParseColor(data["color1"]);
            return data["mode"]?.ToString() switch
            {
                "Color" or "0" or null => first is { } color ? new(color, color) : null,
                "TwoColors" or "2" => new(first ?? Vector4.One, ParseColor(data["color2"]) ?? first ?? Vector4.One),
                "Gradient" or "1" or "RandomColor" or "4" when data["gradient1"] is JObject curve =>
                    new(Vector4.One, Vector4.One, GradientCurve.Parse(curve), RandomTime: data["mode"]?.ToString() is "RandomColor" or "4"),
                "TwoGradients" or "3" when data["gradient1"] is JObject curve =>
                    new(Vector4.One, Vector4.One, GradientCurve.Parse(curve),
                        data["gradient2"] is JObject other ? GradientCurve.Parse(other) : null),
                _ => null,
            };
        }
    }

    private sealed record GradientCurve((float Time, Vector4 Value)[] Colors, (float Time, float Value)[] Alphas, bool Fixed)
    {
        internal static GradientCurve Parse(JObject data) => new(
            ReadKeys(data["colorKeys"], key => ParseColor(key["color"])),
            ReadKeys<float>(data["alphaKeys"], key => Read(key["alpha"], float.NaN) is { } alpha && float.IsFinite(alpha)
                ? Math.Clamp(alpha, 0, 1) : null),
            data["mode"]?.ToString() is "Fixed" or "1");

        internal Vector4 At(float time)
        {
            Vector4 color = Sample(Colors, time, Vector4.One, Vector4.Lerp);
            color.W = Sample(Alphas, time, 1f, Mix);
            return color;
        }

        private T Sample<T>((float Time, T Value)[] keys, float time, T fallback, Func<T, T, float, T> lerp)
        {
            if (keys.Length == 0)
                return fallback;
            if (time <= keys[0].Time)
                return keys[0].Value;
            for (int i = 1; i < keys.Length; i++)
            {
                var (end, next) = keys[i];
                if (time > end)
                    continue;
                var (start, previous) = keys[i - 1];
                return Fixed ? next : lerp(previous, next, (time - start) / (end - start));
            }
            return keys[^1].Value;
        }

        private static (float Time, T Value)[] ReadKeys<T>(JToken? token, Func<JObject, T?> parse) where T : struct
        {
            if (token is not JArray keys)
                return [];
            return keys.OfType<JObject>().Select(key => (Time: Read(key["time"], float.NaN), Value: parse(key)))
                .Where(key => float.IsFinite(key.Time) && key.Value.HasValue)
                .Select(key => (Math.Clamp(key.Time, 0, 1), key.Value!.Value)).OrderBy(key => key.Item1).ToArray();
        }
    }

    private static Vector4? ParseColor(JToken? token) => token?.Type == JTokenType.String
        ? FloorTrackStateResolver.ParseColor((string?)token) : null;
    private static float Mean(Vector2 vector) => (vector.X + vector.Y) * .5f;
    private static float Mix(float from, float to, float p) => from + (to - from) * p;
    private static float Range(Vector2 vector, ref uint state) => Mix(vector.X, vector.Y, Next(ref state));
    private static float Next(ref uint state)
    {
        state = unchecked(state * 1664525u + 1013904223u);
        return (state >> 8) * (1f / 16777216f);
    }
}
