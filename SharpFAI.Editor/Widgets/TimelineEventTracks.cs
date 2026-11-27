using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine;
using SharpFAI.Serialization;
using System.Globalization;

namespace SharpFAI.Editor.Widgets;

internal readonly record struct TimelineEventMarker(double Time, int Floor, string Type, JToken Source, bool Enabled);
internal sealed record TimelineEventTrack(string Label, uint Color, TimelineEventMarker[] Events);
internal readonly record struct TimelineEventGroup(TimelineEventTrack Track, int First, int End)
{
    public int Count => End - First;
}

internal static class TimelineEventTracks
{
    private enum Lane { Timing, Camera, Decoration, Effects }

    public static int Count(bool timing, bool effects) => (timing ? 1 : 0) + (effects ? 3 : 0);

    public static TimelineEventTrack[] Build(EditorDocument chart, LevelRenderer.PlaybackTimeline timing,
        bool showTiming, bool showEffects, double duration)
    {
        if (!showTiming && !showEffects) return [];
        List<TimelineEventMarker> times = [], cameras = [], decorations = [], effects = [];
        foreach (var token in chart.ActionTokens)
            Add(token, decoration: false);
        if (showEffects)
            foreach (var token in chart.Decorations)
                Add(token, decoration: true);

        var rows = new List<TimelineEventTrack>(4);
        if (showTiming)
            rows.Add(Make("TimelineTimingEvents", WidgetDraw.U32(241, 186, 98), times));
        if (showEffects)
        {
            rows.Add(Make("TimelineCameraEvents", WidgetDraw.U32(112, 177, 240), cameras));
            rows.Add(Make("Decorations", WidgetDraw.U32(107, 206, 161), decorations));
            rows.Add(Make("TimelineEffectEvents", WidgetDraw.U32(190, 154, 228), effects));
        }
        return rows.ToArray();

        void Add(JToken token, bool decoration)
        {
            JObject? action = token as JObject;
            int floor;
            string type;
            if (action is not null)
            {
                floor = EditorDocument.Floor(action);
                type = EditorDocument.Type(action);
            }
            else if (TwirlEventToken.TryGetFloor(token, out floor)) type = "Twirl";
            else return;

            if (type.Length == 0 || (uint)floor >= (uint)timing.EntryTimes.Length) return;
            Lane lane = decoration ? Lane.Decoration : Classify(type);
            if (lane == Lane.Timing ? !showTiming : !showEffects) return;
            double bpm = timing.FloorBpms[floor];
            double time = timing.EntryTimes[floor];
            if (lane != Lane.Timing && action?["angleOffset"] is JValue angle &&
                (action["disabled"] as JObject)?["angleOffset"] is not JValue { Value: true })
            {
                double offset = angle.Type is JTokenType.Float or JTokenType.Integer ? angle.Value<double>()
                    : double.TryParse(angle.Value as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;
                time += offset / 180 * (bpm > 1e-6 ? 60 / bpm : .5);
            }
            if (!double.IsFinite(time)) return;
            bool enabled = action?["active"] is not JValue { Value: false };
            var items = lane switch { Lane.Timing => times, Lane.Camera => cameras, Lane.Decoration => decorations, _ => effects };
            items.Add(new(Math.Clamp(time, 0, duration), floor, type, token, enabled));
        }

        static TimelineEventTrack Make(string label, uint color, List<TimelineEventMarker> markers) =>
            new(label, color, markers.OrderBy(item => item.Time).ToArray());
    }

    private static Lane Classify(string type) => type switch
    {
        "SetSpeed" or "Twirl" or "Pause" or "Hold" or "MultiPlanet" or "ScaleRadius" or "Bookmark" or "AutoPlayTiles" => Lane.Timing,
        "MoveCamera" or "ShakeScreen" => Lane.Camera,
        "MoveDecorations" or "SetText" or "SetObject" or "SetParticle" or "EmitParticle" => Lane.Decoration,
        _ => Lane.Effects,
    };

    public static int LowerBound(TimelineEventMarker[] events, double time, int first = 0)
    {
        int last = events.Length;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (events[middle].Time < time) first = middle + 1;
            else last = middle;
        }
        return first;
    }
}
