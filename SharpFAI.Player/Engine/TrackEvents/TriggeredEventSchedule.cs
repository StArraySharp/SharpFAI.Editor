using SharpFAI.Player.Engine.Events;

namespace SharpFAI.Player.Engine;

internal sealed class TriggeredEventSchedule<T>(ScheduledEvent<T>[] authored) where T : TimedEvent
{
    private ScheduledEvent<T>[]? _automatic;
    private List<ScheduledEvent<T>>? _triggered;

    internal ScheduledEvent<T>[] Authored { get; } = authored;
    internal IReadOnlyList<ScheduledEvent<T>> Active =>
        _triggered is { } triggered ? triggered : _automatic ?? Authored;
    internal bool HasBindings => _automatic is not null;
    internal int Revision { get; private set; }
    internal event Action<int, double>? Changed;

    internal void SetAutomatic(ScheduledEvent<T>[]? automatic)
    {
        if (_automatic is null && automatic is null && _triggered is null)
            return;
        _automatic = automatic;
        _triggered = null;
        Invalidate(0, double.NegativeInfinity);
    }

    internal void Insert(ScheduledEvent<T> entry)
    {
        var active = _triggered ??= new List<ScheduledEvent<T>>(_automatic ?? Authored);
        int low = 0, high = active.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (active[middle].Timing.Start <= entry.Timing.Start)
                low = middle + 1;
            else
                high = middle;
        }
        active.Insert(low, entry);
        Invalidate(low, entry.Timing.Start);
    }

    internal void Reset()
    {
        if (_triggered is null)
            return;
        _triggered = null;
        Invalidate(0, double.NegativeInfinity);
    }

    private void Invalidate(int firstChanged, double time)
    {
        Revision++;
        Changed?.Invoke(firstChanged, time);
    }
}
