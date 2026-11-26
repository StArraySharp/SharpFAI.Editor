using System.Text.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.ModApi;

namespace SharpFAI.Editor;

internal sealed partial class EditorDocument
{
    private readonly Guid _modDocumentId = Guid.NewGuid();
    internal ChartRevision ModRevision => new(_modDocumentId, ChangeVersion);
    internal JArray? ModEvents => (_level.root["sharpFaiMods"] as JObject)?["events"] as JArray;

    internal void AddModEvent(string type, CustomEventSchema schema, int floor)
    {
        if (_level.root["sharpFaiMods"] is { } extension && extension is not JObject) return;
        var root = _level.root["sharpFaiMods"] as JObject ?? new JObject { ["formatVersion"] = 1 };
        if (root["events"] is { } existing && existing is not JArray) return;
        var events = root["events"] as JArray ?? new JArray();
        bool addRoot = root.Parent is null, addList = events.Parent is null;
        var payload = new JObject();
        foreach (var field in schema.Fields) payload.Add(field.Key, JToken.Parse(field.Default.GetRawText()));
        var item = new JObject
        {
            ["id"] = Guid.NewGuid().ToString("N"), ["type"] = type, ["schemaVersion"] = schema.Version,
            ["anchor"] = new JObject { ["floor"] = floor, ["angleOffsetDegrees"] = 0 }, ["payload"] = payload,
        };
        Apply(new(() =>
        {
            if (addRoot) _level.root["sharpFaiMods"] = root;
            if (addList) root["events"] = events;
            events.Add(item);
        }, () =>
        {
            item.Remove();
            if (addList)
            {
                root["events"] = JValue.CreateNull();
                root.Remove("events");
            }
            if (addRoot)
            {
                _level.root["sharpFaiMods"] = JValue.CreateNull();
                _level.root.Remove("sharpFaiMods");
            }
        }, Events: false));
    }

    internal void RemoveModEvent(JObject item)
    {
        if (ModEvents is not { } events || item.Parent != events) return;
        int index = events.IndexOf(item);
        Apply(new(() => item.Remove(), () => events.Insert(index, item), Events: false));
    }

    internal ChartView ModView(int selected, bool snapshot)
    {
        ChartRevision revision = ModRevision;
        EditorDocument source = snapshot ? new(Path, _level.CreateSnapshot(PlaybackPath)) : this;
        return new(revision, System.IO.Path.GetFileName(Path), source.LastFloor, selected,
            floor => floor > 0 && floor <= source.LastFloor ? source.IncomingAngle(floor) : null,
            name => source.Settings[name] is { } setting ? ToElement(setting) : null,
            floor => source.Events(floor).Select(ToElement).ToArray());
    }

    private static JsonElement ToElement(JToken source)
    {
        using var json = JsonDocument.Parse(source.ToString(Newtonsoft.Json.Formatting.None));
        return json.RootElement.Clone();
    }

    internal EditResult ApplyModEdits(ChartRevision expected, ChartEditRequest request)
    {
        if (expected != ModRevision) return EditResult.RevisionConflict;
        if (request.Operations.Count == 0) return EditResult.Applied;
        var staged = new EditorDocument(Path, _level.CreateSnapshot(PlaybackPath));
        foreach (var operation in request.Operations)
            if (!staged.ApplyModOperation(operation)) return EditResult.Invalid;
        if (!staged.Modified) return EditResult.Applied;

        var before = Capture(this);
        var after = Capture(staged);
        Apply(new(() => Replace(after), () => Replace(before), Events: true, DecorationList: true));
        return EditResult.Applied;

        static (JToken[] Angles, JToken[] Events, JToken[] Decorations, JProperty[] Settings, JToken? Extensions) Capture(
            EditorDocument document) => (document._angles.ToArray(), document._actions.ToArray(), document._decorations.ToArray(),
            document.Settings.Properties().ToArray(), document._level.root["sharpFaiMods"]);

        void Replace((JToken[] Angles, JToken[] Events, JToken[] Decorations, JProperty[] Settings, JToken? Extensions) data)
        {
            _angles.ReplaceAll(data.Angles);
            _actions.ReplaceAll(data.Events);
            _decorations.ReplaceAll(data.Decorations);
            Settings.ReplaceAll(data.Settings);
            _level.root["sharpFaiMods"] = data.Extensions;
            if (data.Extensions is null) _level.root.Remove("sharpFaiMods");
        }
    }

    private bool ApplyModOperation(ChartOperation operation)
    {
        switch (operation)
        {
            case InsertAngles insertion when insertion.Angles.All(double.IsFinite):
                double[] angles = insertion.Angles.ToArray();
                if (!CanInsertTiles(insertion.AfterFloor, angles)) return false;
                InsertTiles(insertion.AfterFloor, angles);
                return true;
            case DeleteAngles deletion when deletion.FirstFloor > 0 && deletion.Count > 0 &&
                                             deletion.FirstFloor <= LastFloor - deletion.Count + 1:
                DeleteTiles(deletion.FirstFloor, deletion.FirstFloor + deletion.Count - 1);
                return true;
            case SetChartSetting setting when !string.IsNullOrWhiteSpace(setting.Key) &&
                                              setting.Value.ValueKind != JsonValueKind.Undefined:
                if (setting.Key is "bpm" or "pitch" &&
                    (setting.Value.ValueKind != JsonValueKind.Number || !setting.Value.TryGetDouble(out var number) ||
                     !double.IsFinite(number) || number <= 0))
                    return false;
                Set(Settings, setting.Key, JToken.Parse(setting.Value.GetRawText()));
                return true;
            case AddChartEvent addition when addition.Floor >= 0 && addition.Floor <= LastFloor &&
                                             !string.IsNullOrWhiteSpace(addition.Type) &&
                                             addition.Fields.ValueKind == JsonValueKind.Object:
                var item = JObject.Parse(addition.Fields.GetRawText());
                item["floor"] = addition.Floor;
                item["eventType"] = addition.Type;
                return Add(item, decoration: false);
            case UpdateChartEvent update when update.Fields.ValueKind == JsonValueKind.Object &&
                                              Events(update.Floor).ElementAtOrDefault(update.Index) is { } target:
                foreach (var property in JObject.Parse(update.Fields.GetRawText()).Properties())
                {
                    if (property.Name is "floor" or "eventType") return false;
                    Set(target, property.Name, property.Value);
                }
                return true;
            case RemoveChartEvent removal when Events(removal.Floor).ElementAtOrDefault(removal.Index) is { } target:
                Remove(target);
                return true;
            default: return false;
        }
    }
}
