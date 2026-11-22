using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;
using Thrust.Graphics;

namespace SharpFAI.Player.Engine;

internal sealed class BackdropTimeline
{
    internal readonly record struct ImageState(LevelPlaybackSettings Settings, ImageDecoder.RgbaImage? Pixels);
    private readonly record struct Flash(TrackTiming Timing, Vector4 Start, Vector4 End);
    private readonly List<(double Time, ImageState State)> _backgrounds = [];
    private readonly List<(double Time, bool Enabled)> _history = [];
    private readonly List<Flash> _backFlash = [];
    private readonly List<Flash> _frontFlash = [];
    private ScheduledEvent<BackdropEvent>[] _schedule = [];

    private BackdropTimeline(ImageState initial) => Initial = initial;
    public ImageState Initial { get; }
    public IReadOnlyDictionary<string, ImageDecoder.RgbaImage?> Images { get; private init; } =
        new Dictionary<string, ImageDecoder.RgbaImage?>();
    public bool NeedsBackgroundRenderer => Initial.Pixels is not null || Initial.Settings.DefaultPattern ||
        _backgrounds.Count > 0 || _backFlash.Count > 0 || _frontFlash.Count > 0;

    public static BackdropTimeline Build(LevelPlaybackSettings settings, IReadOnlyList<PlaybackEvent> events,
        IReadOnlyList<Floor> floors, double[] times, double[]? bpms, string? directory, List<string> warnings,
        IReadOnlyDictionary<string, ImageDecoder.RgbaImage?>? cached = null)
    {
        var images = new Dictionary<string, ImageDecoder.RgbaImage?>(StringComparer.OrdinalIgnoreCase);
        var initial = new ImageState(settings, Load(settings.BackgroundImage));
        var timeline = new BackdropTimeline(initial)
        {
            Images = images,
            _schedule = TimedEventSchedule.Build<BackdropEvent>(events, floors, times, bpms, preserveChartOrder: true),
        };
        var previous = initial;
        foreach (var scheduled in timeline._schedule)
        {
            var item = scheduled.Event;
            switch (item.EventType)
            {
                case EventType.Flash:
                    timeline.AddFlash(scheduled);
                    break;
                case EventType.HallOfMirrors:
                    timeline._history.Add((scheduled.Timing.Start, item.Enabled("enabled", item.EffectEnabled) ?? true));
                    break;
                case EventType.CustomBackground:
                    string? image = item.Enabled("bgImage", item.Image);
                    var pixels = Load(image);
                    Vector4 color = Color(item.Enabled("color", item.Color), new Vector4(0, 0, 0, 1));
                    if (!string.IsNullOrWhiteSpace(image) && pixels is null)
                    {
                        previous = previous with { Settings = previous.Settings with { Background = color } };
                    }
                    else
                    {
                        previous = new ImageState(settings with
                        {
                            Background = color,
                            BackgroundImage = image,
                            ImageTint = Color(item.Enabled("imageColor", item.ImageColor), Vector4.One),
                            Parallax = PercentPair(item.Enabled("parallax", item.Parallax)),
                            Display = Enum.TryParse<BackgroundDisplay>(item.Enabled("bgDisplayMode", item.Display), true, out var mode)
                                ? mode : BackgroundDisplay.FitToScreen,
                            ImageSmoothing = item.Enabled("imageSmoothing", item.Smoothing) ?? true,
                            LockBackgroundRotation = item.Enabled("lockRot", item.LockRotation) ?? false,
                            LoopBackground = item.Enabled("loopBG", item.Loop) ?? false,
                            BackgroundScale = Math.Max(.001f, (item.Enabled("scalingRatio", item.Scale) ?? 100f) / 100f),
                        }, pixels);
                    }

                    timeline._backgrounds.Add((scheduled.Timing.Start, previous));
                    break;
            }
        }

        return timeline;

        ImageDecoder.RgbaImage? Load(string? name)
        {
            if (directory is null || string.IsNullOrWhiteSpace(name))
                return null;
            if (images.TryGetValue(name, out var loaded))
                return loaded;
            if (cached?.TryGetValue(name, out loaded) == true)
            {
                images.Add(name, loaded);
                return loaded;
            }

            ImageDecoder.RgbaImage? result = null;
            try
            {
                string path = Path.GetFullPath(name.Trim().Replace('\\', Path.DirectorySeparatorChar), directory);
                var image = ImageDecoder.Decode(File.ReadAllBytes(path));
                ImageDecoder.FlipVertically(image.Pixels, image.Width, image.Height);
                result = image;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or SixLabors.ImageSharp.ImageFormatException)
            {
                warnings.Add($"背景图加载失败 {name}：{error.Message}");
            }
            images.Add(name, result);
            return result;
        }
    }

    internal BackdropTimeline? EditFlashes(IReadOnlyDictionary<int, BackdropEvent> replacements,
        IReadOnlyList<Floor> floors, double[]? bpms)
    {
        if (replacements.Values.Any(item => item.EventType != EventType.Flash) ||
            !TimedEventSchedule.TryEdit(_schedule, replacements, floors, bpms, out var edited))
            return null;
        var result = new BackdropTimeline(Initial) { Images = Images, _schedule = edited };
        result._backgrounds.AddRange(_backgrounds);
        result._history.AddRange(_history);
        foreach (var item in edited)
            if (item.Event.EventType == EventType.Flash)
                result.AddFlash(item);
        return result;
    }

    private void AddFlash(ScheduledEvent<BackdropEvent> scheduled)
    {
        var item = scheduled.Event;
        Vector4 start = Color(item.Enabled("startColor", item.StartColor), Vector4.One);
        Vector4 end = Color(item.Enabled("endColor", item.EndColor), Vector4.One);
        start.W = (item.Enabled("startOpacity", item.StartOpacity) ?? 100) / 100f;
        end.W = (item.Enabled("endOpacity", item.EndOpacity) ?? 0) / 100f;
        (item.Enabled("plane", item.Plane) == "Foreground" ? _frontFlash : _backFlash)
            .Add(new Flash(scheduled.Timing, start, end));
    }

    public ImageState BackgroundAt(double time)
    {
        int index = LastAt(_backgrounds, time, item => item.Time);
        return index >= 0 ? _backgrounds[index].State : Initial;
    }

    public bool HistoryAt(double time)
    {
        int index = LastAt(_history, time, item => item.Time);
        return index >= 0 && _history[index].Enabled;
    }

    public Vector4 FlashAt(double time, bool foreground)
    {
        var flashes = foreground ? _frontFlash : _backFlash;
        int index = LastAt(flashes, time, item => item.Timing.Start);
        if (index < 0)
            return Vector4.Zero;
        var flash = flashes[index];
        return Vector4.Lerp(flash.Start, flash.End, flash.Timing.Progress(time));
    }

    private static int LastAt<T>(IReadOnlyList<T> entries, double time, Func<T, double> start)
    {
        int lo = 0;
        int hi = entries.Count;
        while (lo < hi)
        {
            int middle = lo + (hi - lo) / 2;
            if (start(entries[middle]) <= time)
                lo = middle + 1;
            else
                hi = middle;
        }
        return lo - 1;
    }

    private static Vector4 Color(string? value, Vector4 fallback) => FloorTrackStateResolver.ParseColor(value) ?? fallback;

    private static Vector2 PercentPair(JArray? array) => array is { Count: 2 } &&
        array[0].Type is JTokenType.Integer or JTokenType.Float &&
        array[1].Type is JTokenType.Integer or JTokenType.Float
        ? new Vector2(array[0].Value<float>(), array[1].Value<float>()) / 100f : Vector2.One;
}
