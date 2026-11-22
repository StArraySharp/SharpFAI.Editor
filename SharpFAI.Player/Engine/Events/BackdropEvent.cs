using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Player.Engine.Events;

internal sealed class BackdropEvent : TimedEvent
{
    [JsonProperty("plane")] public string? Plane { get; set; }
    [JsonProperty("startColor")] public string? StartColor { get; set; }
    [JsonProperty("startOpacity")] public float? StartOpacity { get; set; }
    [JsonProperty("endColor")] public string? EndColor { get; set; }
    [JsonProperty("endOpacity")] public float? EndOpacity { get; set; }
    [JsonProperty("enabled")] public bool? EffectEnabled { get; set; }
    [JsonProperty("color")] public string? Color { get; set; }
    [JsonProperty("bgImage")] public string? Image { get; set; }
    [JsonProperty("imageColor")] public string? ImageColor { get; set; }
    [JsonProperty("parallax")] public JArray? Parallax { get; set; }
    [JsonProperty("bgDisplayMode")] public string? Display { get; set; }
    [JsonProperty("imageSmoothing")] public bool? Smoothing { get; set; }
    [JsonProperty("lockRot")] public bool? LockRotation { get; set; }
    [JsonProperty("loopBG")] public bool? Loop { get; set; }
    [JsonProperty("scalingRatio")] public float? Scale { get; set; }
}
