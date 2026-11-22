using System.Numerics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine.Filters;

internal sealed class FilterTimeline(ScheduledEvent<CameraFilterEvent>[] schedule, bool legacyTween,
    bool decoration = false) : IDisposable
{
    internal readonly record struct Tween(float From, float To, TrackTiming Timing)
    {
        public float Evaluate(double time) => From + (To - From) * Timing.Progress(time);
    }

    internal readonly record struct ParameterTween(Vector4 From, Vector4 To, TrackTiming Timing)
    {
        public Vector4 Evaluate(double time) => Vector4.Lerp(From, To, Timing.Progress(time));
    }

    internal sealed class Entry(string name, IGeneratedFilter filter, int order)
    {
        private bool _enabled;
        public string Name { get; } = name;
        public IGeneratedFilter Filter { get; } = filter;
        public int Order { get; } = order;
        internal readonly Dictionary<string, ParameterTween> Channels = new(StringComparer.Ordinal);
        internal LegacyFilterBinding? Legacy;
        internal Tween? IntensityTween;
        internal float Intensity;
        internal bool Initialized;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;
                if (value)
                    Filter.Enable();
                else
                    Filter.Dispose();
                _enabled = value;
            }
        }

        public void Apply(double time)
        {
            if (IntensityTween is { } tween)
                Legacy?.SetIntensity?.Invoke(Filter, tween.Evaluate(time));
            foreach (var (field, channel) in Channels)
                Filter.SetParameter(field, channel.Evaluate(time));
        }

        public void FinishLegacy()
        {
            if (IntensityTween is { } previous)
            {
                Intensity = previous.To;
                Legacy?.SetIntensity?.Invoke(Filter, Intensity);
                IntensityTween = null;
            }
        }

        public void FinishAdvanced()
        {
            foreach (var (field, channel) in Channels)
                Filter.SetParameter(field, channel.To);
            Channels.Clear();
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Entry> _ordered = [];
    private readonly List<Entry> _active = [];
    private Dictionary<string, int>? _authoredOrder;
    private int _next;
    private double _previous = double.NegativeInfinity;
    private List<ScheduledEvent<CameraFilterEvent>>? _runtimeSchedule;
    private IReadOnlyList<ScheduledEvent<CameraFilterEvent>> ActiveSchedule =>
        _runtimeSchedule is { } runtime ? runtime : schedule;

    public static FilterTimeline? Build(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors, double[] times,
        double[]? bpms, bool legacyTween)
    {
        if (GeneratedFilterRegistry.Factories.Count == 0 || !events.Any(item => item is CameraFilterEvent))
            return null;
        var schedule = TimedEventSchedule.Build<CameraFilterEvent>(events, floors, times, bpms, preserveChartOrder: true)
            .Where(item => IsForeground(item.Event)).ToArray();
        if (schedule.Length == 0)
            return null;

        Dictionary<string, int> order = new(StringComparer.Ordinal);
        foreach (var item in events.OfType<CameraFilterEvent>())
        {
            if (item.EventType == EventType.SetFilterAdvanced && item.IsActive && IsForeground(item) &&
                item.Enabled("filter", item.Filter) is { Length: > 0 } name)
                order.TryAdd(name, order.Count);
        }
        return new FilterTimeline(schedule, legacyTween) { _authoredOrder = order };
    }

    internal FilterTimeline? Edit(IReadOnlyDictionary<int, CameraFilterEvent> replacements,
        IReadOnlyList<Floor> floors, double[]? bpms)
    {
        if (decoration || _runtimeSchedule is not null || replacements.Values.Any(item => !IsForeground(item)))
            return null;
        foreach (var entry in schedule)
            if (replacements.TryGetValue(entry.Event.SourceIndex, out var replacement) &&
                (entry.Event.Enabled("filter", entry.Event.Filter) != replacement.Enabled("filter", replacement.Filter) ||
                 entry.Event.TargetType != replacement.TargetType || entry.Event.Plane != replacement.Plane))
                return null;
        return TimedEventSchedule.TryEdit(schedule, replacements, floors, bpms, out var edited)
            ? new FilterTimeline(edited, legacyTween) { _authoredOrder = _authoredOrder } : null;
    }

    private static bool IsForeground(CameraFilterEvent item) =>
        item.EventType == EventType.SetFilter || (item.TargetType is null or "Camera" or "0"
            && item.Plane is null or "Foreground" or "0");

    public static FilterTimeline ForDecoration(List<ScheduledEvent<CameraFilterEvent>> schedule,
        Dictionary<string, int> order, bool legacyTween) =>
        new(schedule.ToArray(), legacyTween, decoration: true) { _authoredOrder = order };

    public bool HasDueEvent(double time) => _active.Count != 0 ||
        (time < _previous && _entries.Count != 0) ||
        (_next < ActiveSchedule.Count && ActiveSchedule[_next].Timing.Start <= time);

    public void InsertTriggered(ScheduledEvent<CameraFilterEvent> entry)
    {
        var active = _runtimeSchedule ??= new List<ScheduledEvent<CameraFilterEvent>>(schedule);
        int insert = active.Count;
        while (insert > 0 && active[insert - 1].Timing.Start > entry.Timing.Start)
            insert--;
        active.Insert(insert, entry);
        if (insert < _next)
            ResetFilterState();
    }

    public void ResetTriggers()
    {
        if (_runtimeSchedule is null)
            return;
        _runtimeSchedule = null;
        ResetFilterState();
    }

    public IReadOnlyList<Entry> Evaluate(IFilterBackend backend, double time)
    {
        if (time < _previous)
            ResetFilterState();
        var active = ActiveSchedule;
        bool changed = false;
        while (_next < active.Count && active[_next].Timing.Start <= time)
        {
            ApplyEvent(backend, active[_next++]);
            changed = true;
        }

        _previous = time;
        // Membership and order only change when an event runs. Keep the returned list
        // stable between events instead of walking every filter ever encountered.
        if (changed)
        {
            _active.Clear();
            foreach (Entry entry in _ordered)
                if (entry.Enabled)
                    _active.Add(entry);
        }
        foreach (Entry entry in _active)
            entry.Apply(time);
        return _active;
    }

    private void ApplyEvent(IFilterBackend backend, ScheduledEvent<CameraFilterEvent> scheduled)
    {
        var item = scheduled.Event;
        string? name = item.Enabled("filter", item.Filter);
        if (string.IsNullOrEmpty(name))
            return;

        bool isLegacy = item.EventType == EventType.SetFilter;
        if (!isLegacy && (name.Contains("Blend2Camera_", StringComparison.Ordinal) ||
            name.Contains("Antialiasing_FXAA", StringComparison.Ordinal) ||
            name.Contains("Colors_Adjust_PreFilters", StringComparison.Ordinal)))
            return;

        LegacyFilterBinding? binding = null;
        if (isLegacy)
        {
            if (!GeneratedFilterRegistry.Legacy.TryGetValue(name, out var legacy))
                return;
            binding = legacy;
            name = legacy.Component;
        }

        if (!GeneratedFilterRegistry.Factories.TryGetValue(name, out var create))
            return;

        if (!_entries.TryGetValue(name, out Entry? entry))
        {
            var preinstalled = decoration ? default :
                GeneratedFilterRegistry.Legacy.Values.FirstOrDefault(value => value.Component == name);
            int order = preinstalled.Component is not null ? CameraPassOrder.Legacy(name, preinstalled.Order) :
                _authoredOrder?.TryGetValue(name, out int authored) == true ? int.MaxValue / 2 + authored : int.MaxValue;
            var factory = preinstalled.Component is not null ? GeneratedFilterRegistry.CameraFactories[name] : create;
            entry = new Entry(name, factory(backend), order)
            {
                Legacy = preinstalled.Component is not null ? preinstalled : null,
                Intensity = preinstalled.DefaultIntensity,
            };
            _entries.Add(name, entry);
            int insert = _ordered.FindIndex(other => other.Order > order);
            _ordered.Insert(insert < 0 ? _ordered.Count : insert, entry);
        }

        bool enabled = item.Enabled("enabled", item.FilterEnabled) ?? true;
        bool disableOthers = item.Enabled("disableOthers", item.DisableOthers) == true;
        TrackTiming timing = scheduled.Timing;
        if (isLegacy)
        {
            entry.FinishLegacy();
            var legacy = binding!.Value;
            float to = (item.Enabled("intensity", item.Intensity) ?? 100f) / 100f;
            float from = !entry.Enabled && !legacyTween ? legacy.DefaultIntensity : entry.Intensity;
            entry.Enabled = enabled;
            if (enabled)
            {
                entry.IntensityTween = new Tween(from, to, timing);
                if (timing.Duration <= 0)
                    entry.FinishLegacy();
            }
            else
            {
                entry.Intensity = to;
            }
        }
        else if (enabled)
        {
            entry.Apply(timing.Start);
            foreach (string field in entry.Filter.Parameters)
            {
                var kind = entry.Filter.ParameterKind(field);
                Vector4 initial = kind == FilterParameterKind.Color ? Vector4.One : Vector4.Zero;
                Vector4 current = entry.Filter.GetParameter(field) ?? initial;
                // ffxSetFilterAdvancedPlus uses neutral values on the first event, including
                // omitted fields. Later events retain missing values, while a tween starts
                // from neutral whenever the component was disabled or uninitialized.
                Vector4 target = item.Parameter(field, kind) ?? (entry.Initialized ? current : initial);
                Vector4 from = entry.Initialized && entry.Enabled && !disableOthers ? current : initial;
                entry.Channels[field] = new ParameterTween(from, target, timing);
            }
            entry.Initialized = true;
            entry.Enabled = true;
        }
        else
        {
            entry.FinishAdvanced();
            entry.Enabled = false;
            entry.Initialized = true;
        }

        if (disableOthers)
        {
            foreach (Entry other in _ordered)
            {
                if (other == entry || (isLegacy && other.Legacy is null))
                    continue;
                other.FinishLegacy();
                other.FinishAdvanced();
                other.Enabled = false;
            }
        }
    }

    public void Dispose()
    {
        _runtimeSchedule = null;
        ResetFilterState();
    }

    private void ResetFilterState()
    {
        foreach (Entry entry in _ordered)
            entry.Filter.Dispose();
        _entries.Clear();
        _ordered.Clear();
        _active.Clear();
        _next = 0;
        _previous = double.NegativeInfinity;
    }
}
