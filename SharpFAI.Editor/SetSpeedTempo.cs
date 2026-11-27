using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Events;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Editor;

internal static class SetSpeedTempo
{
    internal static bool TryConvert(LevelRenderer.PlaybackTimeline timeline, int floor, double pitch,
        IReadOnlyList<JObject> events, JObject target, double goal, out (string Key, double Value) result)
    {
        result = default;
        if (!Positive(goal) || timeline.SpeedContexts is not { } contexts || !contexts.TryGetValue(floor, out var context) ||
            timeline.TempoAt(floor, pitch) is not { Effective: double current } tempo)
            return false;

        var serializer = JsonSerializer.Create(EventJsonConverter.GetJsonSettings());
        List<SetSpeed> changes = [];
        SetSpeed? edited = null;
        try
        {
            foreach (var action in events)
            {
                if (EditorDocument.Type(action) != "SetSpeed") continue;
                if (action.ToObject<SetSpeed>(serializer) is not { } change) return false;
                changes.Add(change);
                if (ReferenceEquals(action, target)) edited = change;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        if (edited is null) return false;

        bool multiplier = edited.SpeedType != EventEnums.SpeedType.Bpm;
        double original = multiplier ? edited.BpmMultiplier : edited.BeatsPerMinute;
        double before = Evaluate(original);
        double half = Evaluate(original * .5);
        if (!Positive(original) || !Positive(before) || !Positive(half) ||
            Math.Abs(before / tempo.Authored - 1) > 1e-8)
            return false;

        // Relative tile duration has the form fixed + affected * original / value:
        // each segment's BPM is either independent of the edited event or proportional to its value.
        double affected = before / half - 1;
        double adjustable = current / goal - (1 - affected);
        if (Math.Abs(affected) <= 1e-12) return false;
        double value = original * (affected / adjustable);
        if (!Positive(value)) return false;

        double predicted = current * (Evaluate(value) / before);
        if (!Positive(predicted) || Math.Abs(predicted / goal - 1) > 1e-7) return false;
        result = (multiplier ? "bpmMultiplier" : "beatsPerMinute", value);
        return true;

        double Evaluate(double value)
        {
            if (multiplier) edited.BpmMultiplier = value;
            else edited.BeatsPerMinute = value;
            return context.Bpm * FloorTimingCalculator.ResolveSpeed(context, changes, out _);
        }
    }

    private static bool Positive(double value) => value > 0 && double.IsFinite(value);
}
