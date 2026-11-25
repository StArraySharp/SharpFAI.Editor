using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Editor.Widgets;

internal static class InspectorFields
{
    public static (bool Visible, bool Enabled) GetState(JObject target, EditorField field, EditorField[]? fields, string owner)
    {
        JToken? Read(string key) => target[key] ?? fields?.FirstOrDefault(candidate => candidate.Key == key)?.Default;
        if (owner == "SetSpeed" && field.Key is "beatsPerMinute" or "bpmMultiplier")
        {
            string? speed = Read("speedType")?.ToString();
            if (speed is null or "Bpm" or "Multiplier")
                return (field.Key == (speed == "Multiplier" ? "bpmMultiplier" : "beatsPerMinute"), true);
        }
        return ((!HasRules(field.ShowIf) || Matches(field.ShowIf, Read)) && !Matches(field.HideIf, Read),
            (!HasRules(field.EnableIf) || Matches(field.EnableIf, Read)) && !Matches(field.DisableIf, Read));
    }

    public static IEnumerable<EditorField> OtherFields(JObject target, EditorField[] fields, string owner)
    {
        bool advanced = owner == "SetFilterAdvanced";
        EditorField[] filterFields = advanced ? EditorFilterCatalog.Fields(target.Value<string>("filter") ?? "",
            target.Value<string>("targetType") is not ("Decoration" or "1")) : [];
        foreach (JProperty property in target.Properties())
        {
            string key = property.Name;
            if (key is "floor" or "eventType" or "active" or "locked" or "visible" or "disabled" ||
                fields.Any(field => field.Key == key) ||
                advanced && (key == "filterProperties" || filterFields.Any(field => field.Key == key)))
                continue;
            EditorField inferred = EditorCatalog.Infer(key, property.Value, owner);
            if (GetState(target, inferred, fields, owner).Visible)
                yield return inferred;
        }
    }

    private static bool HasRules(JArray? rules) => rules is { Count: >= 2 };

    private static bool Matches(JArray? rules, Func<string, JToken?> read)
    {
        if (rules is null) return false;
        for (int i = 0; i + 1 < rules.Count; i += 2)
        {
            if (rules[i] is not JValue { Type: JTokenType.String } key || rules[i + 1] is not JValue expected) continue;
            JToken? value = read(key.Value<string>()!);
            if (JToken.DeepEquals(value, expected) || value is JValue scalar && expected.Value is not null &&
                (string.Equals(scalar.ToString(CultureInfo.InvariantCulture), expected.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) ||
                TryBoolean(scalar, out bool actual) && TryBoolean(expected, out bool wanted) && actual == wanted))
                return true;
        }
        return false;
    }

    public static bool TryBoolean(JToken? value, out bool result)
    {
        result = false;
        if (value is not JValue scalar || scalar.Type is not (JTokenType.Boolean or JTokenType.String)) return false;
        string text = scalar.ToString(CultureInfo.InvariantCulture);
        if (bool.TryParse(text, out result)) return true;
        if (string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase)) return result = true;
        return string.Equals(text, "Disabled", StringComparison.OrdinalIgnoreCase);
    }

    public static JValue WithBoolean(JToken? original, bool enabled) => original is { Type: JTokenType.String }
        ? new(bool.TryParse(original.Value<string>(), out _) ? (enabled ? "true" : "false") : (enabled ? "Enabled" : "Disabled"))
        : new(enabled);

    public static JValue? SelectChoice(JToken? original, string option) =>
        original is { Type: JTokenType.String } && original.Value<string>() == option ? null : new JValue(option);

    public static bool IsMultiline(EditorField field) => field.Type is "Text" or "LongText" or "LongString" ||
        field.Type == "String" && field.Key is "comment" or "decText" or "levelDesc" or "levelTitleText" or "congratsText" or "perfectText";

    public static bool UsesRichLevelText(string owner, string key) =>
        owner == "LevelSettings" && key is "artist" or "song" or "levelDesc";

    public static string? AssetType(EditorField field) => field.FileType ?? (field.Key switch
    {
        "songFilename" or "hitsound" => "Audio",
        "bgVideo" => "Video",
        "bgImage" or "decorationImage" or "trackTexture" or "previewImage" or "previewIcon" or "artistPermission" => "Image",
        _ => field.Type == "File" ? "File" : null
    });

    public static string? TagSource(string owner, string key) => (owner, key) switch
    {
        ("RepeatEvents", "tag") or (_, "hitboxEventTag" or "targetEventTag") => "eventTag",
        ("SetConditionalEvents", _) when key.EndsWith("Tag", StringComparison.Ordinal) => "eventTag",
        ("MoveDecorations" or "SetObject" or "SetText" or "SetParticle" or "EmitParticle", "tag") or
        ("SetFilterAdvanced", "targetTag") or (_, "hitboxDecoTag" or "maskingTarget") => "tag",
        _ => null
    };

    public static string MergeTag(string current, string selected) =>
        current.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(selected, StringComparer.Ordinal)
            ? current : current + (current.Length == 0 || current.EndsWith(' ') ? "" : " ") + selected;

    public static JArray? ReadTile(JToken? value)
    {
        if (value is JArray array) return array;
        if (value is null or { Type: JTokenType.Null }) return new JArray(0, "ThisTile");
        if (value is not { Type: JTokenType.String }) return null;
        var pair = value.Value<string>()!.Trim().Trim('(', ')').Split(',', StringSplitOptions.TrimEntries);
        if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset))
            return null;
        return new JArray(offset, pair[1]);
    }

    public static JArray WithTilePart(JArray tile, int component, JToken value)
    {
        var next = (JArray)tile.DeepClone();
        if (next.Count == 0) next.Add(0);
        if (next.Count == 1) next.Add("ThisTile");
        next[component] = value;
        return next;
    }

    public static int? ResolveTile(JArray tile, int floor, int lastFloor)
    {
        if (!int.TryParse(tile.ElementAtOrDefault(0)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset))
            return null;
        int origin = tile.ElementAtOrDefault(1)?.ToString().ToUpperInvariant() switch
        {
            null or "THISTILE" => floor,
            "START" => 0,
            "END" => lastFloor,
            _ => -1
        };
        return origin < 0 ? null : (int)Math.Clamp((long)origin + offset, 0, lastFloor);
    }
}
