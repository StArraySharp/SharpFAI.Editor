using System.Text.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Serialization;
using SharpFAI.Util;

namespace SharpFAI.Editor.Mods;

internal sealed record CompatibleChart(Level Level, int Converted, IReadOnlyList<string> Notices);

internal static class CompatibleChartExport
{
    public static async Task<CompatibleChart> CreateAsync(Level source,
        IReadOnlyList<ModRegistration<CompatibilityConverter>> converters, CancellationToken token)
    {
        var chart = source.CreateSnapshot(source.pathToLevel ?? "untitled.adofai");
        var notices = new List<string>();
        int converted = 0;
        double[]? bpms = null;
        var byType = converters.ToDictionary(entry => entry.Descriptor.Id, StringComparer.Ordinal);
        if (chart.root["sharpFaiMods"] is JObject extension && extension["events"] is JArray events)
        {
            foreach (var item in events)
            {
                token.ThrowIfCancellationRequested();
                if (item is not JObject evt || evt["anchor"] is not JObject anchor ||
                    anchor["floor"] is not JValue { Type: JTokenType.Integer } floorValue ||
                    evt["type"] is not JValue { Value: string type })
                {
                    notices.Add("Invalid Mod event.");
                    continue;
                }
                int floor = floorValue.Value<int>();
                string label = $"#{floor} {type}";
                if (!byType.TryGetValue(type, out var entry))
                {
                    notices.Add(label);
                    continue;
                }
                using var lease = entry.Acquire();
                if (lease is null) { notices.Add(label); continue; }
                try
                {
                    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Lifetime);
                    JsonElement payload = JsonSerializer.Deserialize<JsonElement>(
                        (evt["payload"] ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None));
                    bpms ??= ReadBpms(source, cancel.Token);
                    var input = new CompatibilityEvent(type, floor, anchor.Value<double?>("angleOffsetDegrees") ?? 0,
                        evt.Value<int?>("schemaVersion") ?? 1, payload)
                    {
                        Bpm = (uint)floor < (uint)bpms.Length ? bpms[floor]
                            : source.root["settings"]?["bpm"]?.Value<double>() ?? 100,
                    };
                    var result = await entry.Value.Run(input, cancel.Token).ConfigureAwait(false);
                    cancel.Token.ThrowIfCancellationRequested();
                    if (result is null || result.Actions.Count + (result.Decorations?.Count ?? 0) == 0)
                    {
                        notices.Add(result?.Note is { } note ? $"{label} — {note}" : label);
                        continue;
                    }
                    JObject[] actions = result.Actions.Select(value => ToStock(value, chart.angles.Count, decoration: false)).ToArray();
                    JObject[] decorations = (result.Decorations ?? []).Select(value => ToStock(value, chart.angles.Count, decoration: true)).ToArray();
                    foreach (var action in actions) chart.actions.Add(action);
                    if (decorations.Length != 0)
                    {
                        if (chart.root["decorations"] is not JArray objects) chart.root["decorations"] = objects = new JArray();
                        foreach (var decoration in decorations) objects.Add(decoration);
                    }
                    converted++;
                    if (result.Note is { Length: > 0 } message) notices.Add($"{label} — {message}");
                }
                catch (Exception error)
                {
                    token.ThrowIfCancellationRequested();
                    entry.Report(error);
                    notices.Add($"{label} — {error.GetBaseException().Message}");
                }
            }
        }
        foreach (var property in chart.root.Properties().Where(value =>
                     value.Name.StartsWith("sharpFai", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (!property.Name.Equals("sharpFaiMods", StringComparison.OrdinalIgnoreCase))
                notices.Add(property.Name);
            property.Remove();
        }
        token.ThrowIfCancellationRequested();
        return new(chart, converted, notices);
    }

    private static double[] ReadBpms(Level source, CancellationToken token)
    {
        if (source.angles.Count == 0) return [];
        var core = FloorCoreEvents.Read(source, token);
        var floors = source.CreatePlaybackFloors(core);
        var events = PlaybackEventBinder.BindForEditor(source, token);
        return LevelRenderer.BuildTimeline(source, floors, events, core).FloorBpms;
    }

    private static JObject ToStock(AddChartEvent evt, int lastFloor, bool decoration)
    {
        bool knownDecoration = evt.Type is "AddDecoration" or "AddText" or "AddObject" or "AddParticle";
        if (evt.Floor < 0 || evt.Floor > lastFloor || evt.Fields.ValueKind != JsonValueKind.Object ||
            decoration != knownDecoration || !knownDecoration &&
            (!Enum.TryParse<EventType>(evt.Type, out var type) || type.ToString() != evt.Type))
            throw new InvalidDataException($"'{evt.Type}' is not a recognized native chart event for this slot.");
        var result = JObject.Parse(evt.Fields.GetRawText());
        result["eventType"] = evt.Type;
        result["floor"] = evt.Floor;
        return result;
    }
}
