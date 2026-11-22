using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine.Filters;

namespace SharpFAI.Player.Engine.Events;

internal sealed class CameraFilterEvent : TimedEvent
{
    [JsonProperty("filter")]
    public string? Filter { get; set; }

    [JsonProperty("enabled")]
    public bool? FilterEnabled { get; set; }

    [JsonProperty("disableOthers")]
    public bool? DisableOthers { get; set; }

    [JsonProperty("intensity")]
    public float? Intensity { get; set; }

    [JsonProperty("plane")]
    public string? Plane { get; set; }

    [JsonProperty("targetType")]
    public string? TargetType { get; set; }

    [JsonProperty("targetTag")]
    public string? TargetTag { get; set; }

    private JObject? _filterProperties;

    [JsonProperty("filterProperties")]
    private JToken? FilterProperties
    {
        set => _filterProperties = ParseFilterProperties(value);
    }

    internal static JObject? ParseFilterProperties(JToken? token)
    {
        if (token is JObject properties)
            return properties;
        if (token is not JValue { Value: string encoded })
            return null;

        string text = encoded.Trim();
        if (text.Length == 0)
            return new JObject();
        try
        {
            // Older charts store just the members of the object, without braces.
            return JsonConvert.DeserializeObject<JObject>(text[0] == '{' ? text : "{" + text + "}",
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [JsonExtensionData(ReadData = true, WriteData = false)]
    private IDictionary<string, JToken>? Properties { get; set; }

    internal Vector4? Parameter(string name, FilterParameterKind kind)
    {
        string key = "filter_" + name;
        JToken? token = Properties?.TryGetValue(key, out var flat) == true ? flat : _filterProperties?[key];
        if (Enabled(key, token) is not { } value)
            return null;

        if (kind == FilterParameterKind.Color)
            return value.Type == JTokenType.String && FloorTrackStateResolver.ParseColor((string?)value) is { } color
                ? new Vector4(color.X, color.Y, color.Z, color.W) : null;
        if (kind == FilterParameterKind.Vector2)
            return value is JArray { Count: 2 } vector && Number(vector[0]) is float x && Number(vector[1]) is float y
                ? new Vector4(x, y, 0f, 0f) : null;
        if (Number(value) is not float scalar)
            return null;

        if (kind == FilterParameterKind.Integer)
            scalar = MathF.Truncate(scalar);
        else if (!name.Contains("rotation", StringComparison.OrdinalIgnoreCase))
            scalar /= 100f;
        return new Vector4(scalar, 0f, 0f, 0f);
    }

    private static float? Number(JToken token) =>
        token.Type is JTokenType.Float or JTokenType.Integer && token.Value<float>() is float value && float.IsFinite(value)
            ? value : null;
}
