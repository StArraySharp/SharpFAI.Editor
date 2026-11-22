using System.Numerics;
using Newtonsoft.Json.Linq;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Filters;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal sealed class SceneEffectTimeline : IDisposable
{
    private readonly record struct Sample(TrackTiming Timing, Vector2 From, Vector2 To)
    {
        public Vector2 At(double time) => Vector2.Lerp(From, To, Timing.Progress(time));
    }

    private readonly record struct ScrollSample(double Start, Vector2 Offset, Vector2 Speed)
    {
        public Vector2 At(double time) => Offset + Speed * (float)(time - Start);
    }

    private readonly record struct BloomSample(Sample Amount, FilterTimeline.ParameterTween Tint, bool Enabled);

    private static readonly string[] Planets = ["IcePlanet", "FirePlanet", "GreenPlanet"];
    private readonly List<Sample> _tile = [];
    private readonly List<ScrollSample> _scroll = [];
    private readonly List<Sample>[] _scales = [[], [], []];
    private readonly List<BloomSample> _bloom = [];
    private readonly List<CameraShake> _shakes = [];
    private SceneEffectEvent[] _rotations = [];
    private ScreenPass? _tilePass;
    private ScreenPass? _scrollPass;
    private FilterTimeline.Entry? _bloomPass;

    public static SceneEffectTimeline? Build(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors,
        double[] times, double[]? bpms)
    {
        if (!events.Any(item => item is SceneEffectEvent))
            return null;
        var timeline = new SceneEffectTimeline();
        timeline._rotations = events.OfType<SceneEffectEvent>()
            .Where(item => item.EventType == EventType.SetPlanetRotation && item.IsActive && (uint)item.Floor < (uint)floors.Count)
            .OrderBy(item => item.Floor).ToArray();
        foreach (var entry in TimedEventSchedule.Build<SceneEffectEvent>(events, floors, times, bpms, preserveChartOrder: true))
        {
            var item = entry.Event;
            TrackTiming timing = entry.Timing;
            switch (item.EventType)
            {
                case EventType.ShakeScreen:
                    if (timing.Duration > 0 && double.IsFinite(timing.Duration))
                    {
                        float strength = item.Enabled("strength", item.Strength) ?? 0f;
                        float intensity = item.Enabled("intensity", item.Intensity) ?? 100f;
                        if (float.IsFinite(strength) && float.IsFinite(intensity))
                            timeline._shakes.Add(new CameraShake(timing, strength, intensity / 100f,
                                item.Enabled("fadeOut", item.FadeOut) ?? true, timeline._shakes.Count));
                    }
                    break;
                case EventType.ScreenTile:
                    Vector2 to = ReadVector(item.Enabled("tile", item.Tile), Vector2.One);
                    Vector2 from = timeline._tile.Count == 0 ? Vector2.One : timeline._tile[^1].To;
                    timeline._tile.Add(new Sample(timing, from, to));
                    break;
                case EventType.ScreenScroll:
                    Vector2 speed = ReadVector(item.Enabled("scroll", item.Scroll), Vector2.Zero) / 100f;
                    Vector2 offset = timeline._scroll.Count == 0 || timeline._scroll[^1].Speed == Vector2.Zero
                        ? Vector2.Zero : timeline._scroll[^1].At(timing.Start);
                    timeline._scroll.Add(new ScrollSample(timing.Start, offset, speed));
                    break;
                case EventType.ScalePlanets:
                    float scale = (item.Enabled("scale", item.Scale) ?? 100f) / 100f;
                    string target = item.Enabled("targetPlanet", item.TargetPlanet) ?? "FirePlanet";
                    for (int index = 0; index < Planets.Length; index++)
                    {
                        if (target != "All" && target != Planets[index])
                            continue;
                        var channel = timeline._scales[index];
                        Vector2 current = channel.Count == 0 ? Vector2.One : channel[^1].At(timing.Start);
                        channel.Add(new Sample(timing, current, new Vector2(scale)));
                    }
                    break;
                case EventType.Bloom:
                    Vector2 previous = timeline._bloom.Count == 0 ? new Vector2(.75f, .5f) : timeline._bloom[^1].Amount.To;
                    Vector4 previousTint = timeline._bloom.Count == 0 ? Vector4.One : timeline._bloom[^1].Tint.To;
                    Vector2 amount = new((item.Enabled("threshold", item.Threshold) ?? 75f) / 100f,
                        (item.Enabled("intensity", item.Intensity) ?? 50f) / 100f);
                    Vector4 tint = Vector4.One;
                    if (FloorTrackStateResolver.ParseColor(item.Enabled("color", item.Color)) is { } color)
                        tint = new Vector4(color.X, color.Y, color.Z, color.W);
                    timeline._bloom.Add(new BloomSample(new Sample(timing, previous, amount),
                        new FilterTimeline.ParameterTween(previousTint, tint, timing), item.Enabled("enabled", item.EffectEnabled) ?? true));
                    break;
            }
        }
        return timeline;
    }

    public float PlanetScale(int planet, double time) =>
        Read(_scales[planet], time, Vector2.One).X;

    public OpenTK.Mathematics.Vector2 ShakeAt(double time)
    {
        int index = IndexAt(_shakes, time, item => item.Start);
        return index < 0 ? OpenTK.Mathematics.Vector2.Zero : _shakes[index].At(time);
    }

    public SceneEffectEvent? PlanetRotation(int floor)
    {
        int index = IndexAt(_rotations, floor, item => item.Floor);
        return index >= 0 ? _rotations[index] : null;
    }

    public bool HasScreenEffect(double time)
    {
        int index = IndexAt(_scroll, time, item => item.Start);
        return FindBloom(time) is { Enabled: true } || Read(_tile, time, Vector2.One) != Vector2.One ||
            (index >= 0 && _scroll[index].Speed != Vector2.Zero);
    }

    public void AppendScreenPasses(OpenGlFilterBackend backend, double time, List<FilterTimeline.Entry> active)
    {
        if (FindBloom(time) is { Enabled: true } bloom)
        {
            _bloomPass ??= new FilterTimeline.Entry(VideoBloomPass.ShaderName, new VideoBloomPass(backend),
                CameraPassOrder.Legacy(VideoBloomPass.ShaderName, 0));
            var filter = (VideoBloomPass)_bloomPass.Filter;
            Vector2 amount = bloom.Amount.At(time);
            filter.Threshold = amount.X;
            filter.Intensity = amount.Y;
            filter.Tint = bloom.Tint.Evaluate(time);
            active.Add(_bloomPass);
        }
        Vector2 tile = Read(_tile, time, Vector2.One);
        if (tile != Vector2.One)
        {
            _tilePass ??= new ScreenPass(backend, "ADOFAI/ScreenTile", "_TileX", "_TileY");
            _tilePass.Value = tile;
            active.Add(_tilePass.Entry);
        }

        int index = IndexAt(_scroll, time, item => item.Start);
        if (index >= 0 && _scroll[index].Speed != Vector2.Zero)
        {
            _scrollPass ??= new ScreenPass(backend, "ADOFAI/ScreenScroll", "_OffsetX", "_OffsetY");
            _scrollPass.Value = _scroll[index].At(time);
            active.Add(_scrollPass.Entry);
        }
    }

    private BloomSample? FindBloom(double time)
    {
        int index = IndexAt(_bloom, time, item => item.Amount.Timing.Start);
        return index >= 0 ? _bloom[index] : null;
    }

    private static Vector2 Read(List<Sample> samples, double time, Vector2 fallback)
    {
        int index = IndexAt(samples, time, item => item.Timing.Start);
        return index < 0 ? fallback : samples[index].At(time);
    }

    private static int IndexAt<T>(IReadOnlyList<T> entries, double time, Func<T, double> start)
    {
        int low = 0;
        int high = entries.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (start(entries[middle]) <= time)
                low = middle + 1;
            else
                high = middle;
        }
        return low - 1;
    }

    private static Vector2 ReadVector(JArray? value, Vector2 fallback)
    {
        if (value is not { Count: 2 } ||
            value[0].Type is not (JTokenType.Integer or JTokenType.Float) ||
            value[1].Type is not (JTokenType.Integer or JTokenType.Float))
            return fallback;
        float x = value[0].Value<float>();
        float y = value[1].Value<float>();
        return float.IsFinite(x) && float.IsFinite(y) ? new Vector2(x, y) : fallback;
    }

    public void Dispose()
    {
        _tilePass?.Dispose();
        _scrollPass?.Dispose();
        _bloomPass?.Filter.Dispose();
    }

    private sealed class ScreenPass : IGeneratedFilter
    {
        private readonly IFilterBackend _backend;
        private readonly IFilterMaterial _material;
        private readonly string _x;
        private readonly string _y;
        public FilterTimeline.Entry Entry { get; }
        public Vector2 Value { get; set; }

        public ScreenPass(IFilterBackend backend, string shader, string x, string y)
        {
            _backend = backend;
            _material = backend.CreateMaterial(backend.FindShader(shader)!);
            _x = x;
            _y = y;
            Entry = new FilterTimeline.Entry(shader, this, CameraPassOrder.Legacy(shader, 0));
        }

        public void Render(IFilterTarget source, IFilterTarget destination)
        {
            _material.SetFloat(_x, Value.X);
            _material.SetFloat(_y, Value.Y);
            _material.SetVector("_ScreenResolution", new Vector4(source.Width, source.Height, 0f, 0f));
            _backend.Blit(source, destination, _material);
        }

        public IReadOnlyList<string> Parameters => [];
        public FilterParameterKind ParameterKind(string field) => FilterParameterKind.Float;
        public Vector4? GetParameter(string field) => null;
        public void SetParameter(string field, Vector4 value) { }
        public void Enable() { }
        public void Dispose() => _backend.ReleaseMaterial(_material);
    }
}
