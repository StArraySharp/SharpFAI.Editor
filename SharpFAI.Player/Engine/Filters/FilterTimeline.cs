using System.Numerics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine.Filters;

internal sealed class FilterTimeline(ScheduledEvent<CameraFilterEvent>[] schedule, bool legacyTween,
    bool background = false) : IDisposable
{
    internal readonly record struct Tween(float From, float To, TrackTiming Timing)
    {
        public float Evaluate(double time) => From + (To - From) * Timing.Progress(time);
    }

    internal readonly record struct ParameterTween(Vector4 From, Vector4 To, TrackTiming Timing)
    {
        public Vector4 Evaluate(double time) => From + (To - From) * Timing.Progress(time);
    }

    internal sealed class Entry(string name, IGeneratedFilter filter, int order)
    {
        private bool _enabled;
        public string Name { get; } = name;
        public IGeneratedFilter Filter { get; } = filter;
        public int Order { get; } = order;
        internal readonly Dictionary<string, ParameterTween> Channels = new(StringComparer.Ordinal);
        internal Dictionary<string, Vector4>? OriginalValues;
        internal bool Initialized;
        internal bool UsedByAdvanced;

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

        public void AdvanceAdvanced(double time)
        {
            if (Channels.Count == 0)
                return;
            bool completed = false;
            foreach (var (field, channel) in Channels)
            {
                if (time <= channel.Timing.Start)
                    continue;
                completed = time >= channel.Timing.Start + channel.Timing.Duration;
                Filter.SetParameter(field, completed ? channel.To : channel.Evaluate(time));
            }
            if (completed)
                Channels.Clear();
        }

        public void FinishAdvanced()
        {
            foreach (var (field, channel) in Channels)
                Filter.SetParameter(field, channel.To);
            Channels.Clear();
        }
    }

    private sealed class LegacyState(Entry entry, LegacyFilterBinding binding)
    {
        private (Tween Tween, bool Enable)? _pending;
        private float _intensity = binding.DefaultIntensity;

        public bool HasTween => _pending is not null;

        public void Start(float target, bool enabled, TrackTiming timing, bool legacyTween)
        {
            Finish(timing.Start);
            if (timing.Duration <= 0)
            {
                Set(target, enabled, timing.Start);
                return;
            }
            float from = !entry.Enabled && !legacyTween ? binding.DefaultIntensity : _intensity;
            _pending = (new Tween(from, target, timing), enabled);
        }

        public bool Advance(double time)
        {
            if (_pending is not { } pending || time <= pending.Tween.Timing.Start)
                return false;
            bool wasEnabled = entry.Enabled;
            if (time >= pending.Tween.Timing.Start + pending.Tween.Timing.Duration)
                Finish(time);
            else
                Set(pending.Tween.Evaluate(time), pending.Enable, time);
            return entry.Enabled != wasEnabled;
        }

        public void Finish(double time)
        {
            if (_pending is not { } pending)
                return;
            _pending = null;
            Set(pending.Tween.To, pending.Enable, time);
        }

        private void Set(float intensity, bool enabled, double time)
        {
            _intensity = intensity;
            entry.Enabled = enabled;
            if (enabled)
            {
                binding.SetIntensity?.Invoke(entry.Filter, intensity);
                binding.OnEnabled?.Invoke(entry.Filter, time);
            }
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyState> _legacyStates = new(StringComparer.Ordinal);
    private readonly List<Entry> _ordered = [];
    private readonly List<Entry> _active = [];
    private Dictionary<string, int>? _authoredOrder;
    private int _next;
    private double _previous = double.NegativeInfinity;
    private ScheduledEvent<CameraFilterEvent>[]? _automaticSchedule;
    private List<ScheduledEvent<CameraFilterEvent>>? _runtimeSchedule;
    internal IReadOnlyList<ScheduledEvent<CameraFilterEvent>> AuthoredSchedule => schedule;
    private ScheduledEvent<CameraFilterEvent>[] AutomaticSchedule => _automaticSchedule ?? schedule;
    private IReadOnlyList<ScheduledEvent<CameraFilterEvent>> ActiveSchedule =>
        _runtimeSchedule is { } runtime ? runtime : AutomaticSchedule;

    public static FilterTimeline? Build(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors, double[] times,
        double[]? bpms, bool legacyTween, bool background = false)
    {
        if (!events.Any(item => item is CameraFilterEvent))
            return null;
        var schedule = TimedEventSchedule.Build<CameraFilterEvent>(events, floors, times, bpms)
            .Where(item => IsBackground(item.Event) == background).ToArray();
        if (schedule.Length == 0)
            return null;

        Dictionary<string, int> order = new(StringComparer.Ordinal);
        foreach (var item in events.OfType<CameraFilterEvent>())
        {
            if (item.EventType == EventType.SetFilterAdvanced && item.IsActive && IsBackground(item) == background &&
                FilterName(item) is { Length: > 0 } name)
                order.TryAdd(name, order.Count);
        }
        return new FilterTimeline(schedule, legacyTween, background: background) { _authoredOrder = order };
    }

    internal FilterTimeline? Edit(IReadOnlyDictionary<int, CameraFilterEvent> replacements,
        IReadOnlyList<Floor> floors, double[]? bpms)
    {
        if (_runtimeSchedule is not null || replacements.Values.Any(item => IsBackground(item) != background))
            return null;
        foreach (var entry in schedule)
            if (replacements.TryGetValue(entry.Event.SourceIndex, out var replacement) &&
                (FilterName(entry.Event) != FilterName(replacement) ||
                 IsBackground(entry.Event) != IsBackground(replacement)))
                return null;
        return TimedEventSchedule.TryEdit(schedule, replacements, floors, bpms, out var edited)
            ? new FilterTimeline(edited, legacyTween, background: background) { _authoredOrder = _authoredOrder } : null;
    }

    private static bool IsBackground(CameraFilterEvent item) => item.EventType == EventType.SetFilterAdvanced &&
        item.Plane is "Background" or "1";

    private static string FilterName(CameraFilterEvent item)
    {
        bool legacy = item.EventType == EventType.SetFilter;
        string name = item.Enabled("filter", item.Filter) ?? (legacy ? "Grayscale" : "CameraFilterPack_AAA_SuperComputer");
        return legacy ? LegacyCameraFilters.DecodeName(name) : name;
    }

    /// <summary>Advanced filters that have no shader of their own and are skipped at apply time.</summary>
    private static bool IsUnsupported(string name) =>
        name.Contains("Blend2Camera_", StringComparison.Ordinal) ||
        name.Contains("Antialiasing_FXAA", StringComparison.Ordinal) ||
        name.Contains("Colors_Adjust_PreFilters", StringComparison.Ordinal);

    /// <summary>
    /// The Unity shader names this chart installs, in the order it first needs them.
    /// </summary>
    /// <remarks>
    /// A driver links a filter program the first time it draws, which costs tens of milliseconds and
    /// shows up as a hitch mid-playback. Walking the schedule up front lets the caller compile the
    /// programs while the level is still idle.
    /// </remarks>
    private IEnumerable<string> ComponentNames()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var scheduled in schedule)
        {
            string name = FilterName(scheduled.Event);
            if (string.IsNullOrEmpty(name))
                continue;
            if (scheduled.Event.EventType == EventType.SetFilter)
            {
                if (!LegacyCameraFilters.Bindings.TryGetValue(name, out var legacy))
                    continue;
                name = legacy.Component;
            }
            else if (IsUnsupported(name))
                continue;
            if (seen.Add(name))
                yield return name;
        }
    }

    public IEnumerable<string> ComponentNamesForWarmup() => ComponentNames();

    /// <summary>Applies every scheduled event once so the parameter parsing and switch logic is warm.</summary>
    public void PrewarmSchedule(IFilterBackend backend)
    {
        var scratch = new FilterTimeline(schedule, legacyTween, background: background) { _authoredOrder = _authoredOrder };
        try
        {
            foreach (var scheduled in schedule)
                scratch.Evaluate(backend, scheduled.Timing.Start);
        }
        catch (Exception)
        {
            // Warmup is best effort: the live path reports its own resource errors.
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Instantiates one filter component for <paramref name="name"/> and exercises it once.
    /// </summary>
    /// <remarks>
    /// The generated filter classes are large and first use pays for JIT compiling them plus the
    /// driver specializing the program for its real uniform state. Doing that while the level loads
    /// keeps the first in-chart switch from stalling a frame.
    /// </remarks>
    public void PrewarmComponent(IFilterBackend backend, string name, int width, int height)
    {
        if (!TryCreate(name, out var create))
            return;
        width = Math.Max(8, width);
        height = Math.Max(8, height);
        IGeneratedFilter? filter = null;
        IFilterTarget? source = null;
        IFilterTarget? destination = null;
        try
        {
            filter = create(backend);
            filter.Enable();
            foreach (string field in filter.Parameters)
            {
                _ = filter.ParameterKind(field);
                filter.SetParameter(field, filter.GetParameter(field) ?? Vector4.Zero);
            }
            source = backend.RentTarget(width, height, 0);
            destination = backend.RentTarget(width, height, 0);
            filter.Render(source, destination);
        }
        catch (Exception)
        {
            // Warmup is best effort: the live path reports its own resource errors.
        }
        finally
        {
            filter?.Dispose();
            if (source is not null)
                backend.ReleaseTarget(source);
            if (destination is not null)
                backend.ReleaseTarget(destination);
        }
    }

    private bool TryCreate(string name, out Func<IFilterBackend, IGeneratedFilter> create)
    {
        if (GeneratedFilterRegistry.Factories.TryGetValue(name, out create!))
        {
            if (!background && LegacyCameraFilters.Components.Contains(name) &&
                GeneratedFilterRegistry.CameraFactories.TryGetValue(name, out var cameraFactory))
                create = cameraFactory;
            return true;
        }
        return LegacyCameraFilters.Factories.TryGetValue(name, out create!);
    }

    public IEnumerable<string> ShaderNames()
    {
        foreach (string name in ComponentNames())
            if (GeneratedFilterRegistry.ShaderNames.TryGetValue(name, out string? shader) ||
                LegacyCameraFilters.ShaderNames.TryGetValue(name, out shader))
                yield return shader;
    }

    public IEnumerable<string> TextureNames()
    {
        foreach (string name in ComponentNames())
        {
            if (GeneratedFilterRegistry.TextureNames.TryGetValue(name, out var names))
                foreach (string texture in names)
                    yield return texture;
            if (name == "CameraMotionBlur")
                yield return "MotionBlurJitter";
            else if (name == "FallingPetals")
            {
                yield return "Rotating_Petals_1";
                yield return "Rotating_Petals_2";
                yield return "Rotating_Petals_3";
            }
        }
    }

    internal void SetAutomaticSchedule(ScheduledEvent<CameraFilterEvent>[]? automatic)
    {
        _automaticSchedule = automatic;
        _runtimeSchedule = null;
        ResetFilterState();
    }

    public bool HasDueEvent(double time) => _active.Count != 0 ||
        _legacyStates.Values.Any(state => state.HasTween) ||
        (time < _previous && _entries.Count != 0) ||
        (_next < ActiveSchedule.Count && ActiveSchedule[_next].Timing.Start <= time);

    public void InsertTriggered(ScheduledEvent<CameraFilterEvent> entry)
    {
        var active = _runtimeSchedule ??= new List<ScheduledEvent<CameraFilterEvent>>(AutomaticSchedule);
        int insert = active.Count;
        while (insert > 0 && active[insert - 1].Timing.Start > entry.Timing.Start)
            insert--;
        active.Insert(insert, entry);
        if (insert < _next || entry.Timing.Start < _previous)
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
            var scheduled = active[_next++];
            changed |= Advance(scheduled.Timing.Start);
            ApplyEvent(backend, scheduled);
            changed = true;
        }

        changed |= Advance(time);
        _previous = time;
        if (changed)
        {
            _active.Clear();
            foreach (Entry entry in _ordered)
                if (entry.Enabled)
                    _active.Add(entry);
        }
        return _active;
    }

    private double _lastTweenSample = double.NegativeInfinity;

    private bool Advance(double time)
    {
        if (time <= _lastTweenSample)
            return false;
        _lastTweenSample = time;
        bool changed = false;
        foreach (var legacy in _legacyStates.Values)
            changed |= legacy.Advance(time);
        foreach (Entry entry in _ordered)
            entry.AdvanceAdvanced(time);
        return changed;
    }

    private void ApplyEvent(IFilterBackend backend, ScheduledEvent<CameraFilterEvent> scheduled)
    {
        var item = scheduled.Event;
        string name = FilterName(item);
        if (string.IsNullOrEmpty(name))
            return;

        bool isLegacy = item.EventType == EventType.SetFilter;
        if (!isLegacy && IsUnsupported(name))
            return;

        var filterName = name;
        LegacyFilterBinding binding = default;
        if (isLegacy)
        {
            if (!LegacyCameraFilters.Bindings.TryGetValue(name, out binding))
                return;
            name = binding.Component;
        }

        if (!GeneratedFilterRegistry.Factories.TryGetValue(name, out var create) &&
            !(isLegacy && LegacyCameraFilters.Factories.TryGetValue(name, out create)))
            return;

        if (!_entries.TryGetValue(name, out Entry? entry))
        {
            bool preinstalled = !background && LegacyCameraFilters.Components.Contains(name);
            int order = preinstalled ? CameraPassOrder.Legacy(name, binding.Order) :
                _authoredOrder?.TryGetValue(name, out int authored) == true ? int.MaxValue / 2 + authored : int.MaxValue;
            if (preinstalled && GeneratedFilterRegistry.CameraFactories.TryGetValue(name, out var cameraFactory))
                create = cameraFactory;
            entry = new Entry(name, create(backend), order);
            _entries.Add(name, entry);
            int insert = _ordered.FindIndex(other => other.Order > order);
            _ordered.Insert(insert < 0 ? _ordered.Count : insert, entry);
        }

        bool enabled = item.Enabled("enabled", item.FilterEnabled) ?? true;
        bool disableOthers = item.Enabled("disableOthers", item.DisableOthers) == true;
        TrackTiming timing = scheduled.Timing;
        if (isLegacy)
        {
            if (!_legacyStates.TryGetValue(filterName, out LegacyState? state))
                _legacyStates.Add(filterName, state = new LegacyState(entry, binding));
            float to = (item.Enabled("intensity", item.Intensity) ?? 100f) / 100f;
            state.Start(to, enabled, timing, legacyTween);
            if (disableOthers)
            {
                foreach (var (otherName, otherBinding) in LegacyCameraFilters.Bindings)
                {
                    if (otherName == filterName || !_entries.TryGetValue(otherBinding.Component, out Entry? other))
                        continue;
                    if (_legacyStates.TryGetValue(otherName, out var otherState))
                        otherState.Finish(timing.Start);
                    other.Enabled = false;
                }
            }
            return;
        }

        if (disableOthers)
        {
            foreach (Entry other in _ordered)
            {
                if (!other.UsedByAdvanced)
                    continue;
                other.Enabled = false;
                if (other.OriginalValues is { } values)
                    foreach (var (field, original) in values)
                        other.Filter.SetParameter(field, original);
                other.FinishAdvanced();
                other.UsedByAdvanced = false;
            }
        }

        if (enabled)
        {
            if (!background && LegacyCameraFilters.Components.Contains(name))
                entry.OriginalValues ??= entry.Filter.Parameters
                    .Where(field => entry.Filter.GetParameter(field).HasValue)
                    .ToDictionary(field => field, field => entry.Filter.GetParameter(field)!.Value, StringComparer.Ordinal);
            foreach (string field in entry.Filter.Parameters)
            {
                var kind = entry.Filter.ParameterKind(field);
                Vector4 initial = kind == FilterParameterKind.Color ? Vector4.One : Vector4.Zero;
                Vector4 current = entry.Filter.GetParameter(field) ?? initial;
                Vector4 target = item.Parameter(field, kind) ?? (entry.Initialized ? current : initial);
                if (timing.Duration <= 0)
                {
                    entry.Filter.SetParameter(field, target);
                    continue;
                }
                Vector4 from = entry.Initialized && entry.Enabled ? current : initial;
                entry.Filter.SetParameter(field, entry.Channels.TryGetValue(field, out var old) ? old.To : from);
                entry.Channels[field] = new ParameterTween(from, target, timing);
            }
        }
        else
        {
            entry.FinishAdvanced();
        }
        entry.Enabled = enabled;
        entry.Initialized = true;
        entry.UsedByAdvanced = true;
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
        _legacyStates.Clear();
        _ordered.Clear();
        _active.Clear();
        _next = 0;
        _previous = double.NegativeInfinity;
        _lastTweenSample = double.NegativeInfinity;
    }
}
