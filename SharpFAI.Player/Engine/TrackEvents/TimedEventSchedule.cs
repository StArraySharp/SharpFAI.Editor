using SharpFAI.Player.Engine.Events;
using SharpFAI.Framework;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal readonly record struct ScheduledEvent<T>(T Event, int Anchor, TrackTiming Timing, double AngleOffset = 0)
    where T : TimedEvent;

internal static class TimedEventSchedule
{
    private readonly record struct Repetition(int Count, double Interval, int Stride, bool UseCurrentFloor);

    public static ScheduledEvent<T>[] Build<T>(IReadOnlyList<PlaybackEvent> events, IReadOnlyList<Floor> floors,
        double[] entries, double[]? floorBpms, bool preserveChartOrder = false) where T : TimedEvent
    {
        if (floors.Count == 0)
        {
            return [];
        }

        Dictionary<(int Floor, string Tag), Repetition> repetitions = [];
        foreach (RepeatEvents item in events.OfType<RepeatEvents>().Where(item => item.IsActive))
        {
            bool byFloor = item.Enabled("repeatType", item.RepeatType) == "Floor";
            int count = byFloor ? item.Enabled("floorCount", item.FloorCount) ?? 0
                : item.Enabled("repetitions", item.Repetitions) ?? 0;
            double interval = byFloor ? 0 : Math.Max(0, item.Enabled("interval", item.Interval) ?? 1f);
            if (!byFloor && interval <= 0)
            {
                continue;
            }

            Repetition repeat = new(count, interval, Math.Max(1, item.Enabled("gapLength", item.GapLength) ?? 1),
                item.Enabled("executeOnCurrentFloor", item.ExecuteOnCurrentFloor) == true);
            foreach (string tag in SplitTags(item.Enabled("tag", item.Tag)))
            {
                repetitions[(item.Floor, tag)] = repeat;
            }
        }

        var conditions = repetitions.Count == 0 ? null : ImageDecorationTimeline.ReadConditions(events);
        List<ScheduledEvent<T>> scheduled = [];
        foreach (T item in events.OfType<T>())
        {
            int source = item.Floor;
            if (!item.IsActive || (uint)source >= (uint)floors.Count)
            {
                continue;
            }

            Repetition repeat = default;
            foreach (string tag in SplitTags(item.Enabled("eventTag", item.EventTag)))
            {
                if (repetitions.TryGetValue((source, tag), out repeat))
                {
                    break;
                }
            }

            float angle = item.Enabled("angleOffset", item.AngleOffset) ?? 0;
            float duration = item.Enabled("duration", item.Duration) ?? 0;
            EventEnums.Ease ease = item.Enabled("ease", item.Ease) ?? EventEnums.Ease.Linear;
            // The game registers only the original copy when its effect is judgment-bound.
            int count = repeat.Count > 0 && (conditions is null ||
                !ImageDecorationTimeline.TryGetCondition(item, conditions, out _)) ? repeat.Count : 0;
            int previousFloor = source;
            double repeatedBeats = 0;
            for (long i = 0; i <= count; i++)
            {
                long movedFloor = source + (repeat.Interval > 0 ? 0 : i * Math.Max(1, repeat.Stride));
                if (movedFloor >= floors.Count)
                {
                    break;
                }

                int target = repeat.UseCurrentFloor ? (int)movedFloor : source;
                double bpm = floorBpms?[target] ?? floors[target].bpm;
                double beat = bpm > 1e-6 ? 60.0 / bpm : 0.5;
                // A repeated event owned by its original tile converts the intervening
                // angular distance using that tile's BPM, including across speed changes.
                if (!repeat.UseCurrentFloor && repeat.Interval == 0)
                {
                    for (; previousFloor < movedFloor; previousFloor++)
                    {
                        double segmentBpm = floorBpms?[previousFloor] ?? floors[previousFloor].bpm;
                        repeatedBeats += (entries[previousFloor + 1] - entries[previousFloor]) *
                            (segmentBpm > 1e-6 ? segmentBpm / 60.0 : 2.0);
                    }
                }
                double offset = angle / 180.0 + repeat.Interval * i + repeatedBeats;
                double start = entries[target] + offset * beat;
                if (i > 0 && repeat.Interval > 0 && start > entries[^1])
                {
                    break;
                }

                scheduled.Add(new ScheduledEvent<T>(item, target, new TrackTiming(start, Math.Max(0, duration) * beat, ease), offset));
            }
        }

        var ordered = scheduled.OrderBy(item => item.Timing.Start);
        return (preserveChartOrder ? ordered : ordered.ThenBy(item => item.Anchor)).ToArray();
    }

    internal static bool TryEdit<T>(ScheduledEvent<T>[] previous, IReadOnlyDictionary<int, T> replacements,
        IReadOnlyList<Floor> floors, double[]? bpms, out ScheduledEvent<T>[] result) where T : TimedEvent
    {
        result = previous;
        HashSet<int> found = [];
        for (int index = 0; index < previous.Length; index++)
        {
            var entry = previous[index];
            if (!replacements.TryGetValue(entry.Event.SourceIndex, out var replacement))
                continue;
            var old = entry.Event;
            if (old.IsActive != replacement.IsActive || old.Floor != replacement.Floor ||
                old.Enabled("angleOffset", old.AngleOffset) != replacement.Enabled("angleOffset", replacement.AngleOffset) ||
                old.Enabled("eventTag", old.EventTag) != replacement.Enabled("eventTag", replacement.EventTag))
            {
                result = previous;
                return false;
            }
            if (ReferenceEquals(result, previous))
                result = (ScheduledEvent<T>[])previous.Clone();
            double bpm = bpms?[entry.Anchor] ?? floors[entry.Anchor].bpm;
            double beat = bpm > 1e-6 ? 60.0 / bpm : .5;
            result[index] = entry with
            {
                Event = replacement,
                Timing = entry.Timing with
                {
                    Duration = Math.Max(0, replacement.Enabled("duration", replacement.Duration) ?? 0) * beat,
                    Ease = replacement.Enabled("ease", replacement.Ease) ?? EventEnums.Ease.Linear,
                },
            };
            found.Add(replacement.SourceIndex);
        }
        return found.Count == replacements.Count;
    }

    private static string[] SplitTags(string? text) => text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
}
