using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Player.Engine.Events;

internal sealed class SceneEffectEvent : TimedEvent
{
    [JsonProperty("tile")] public JArray? Tile { get; set; }
    [JsonProperty("scroll")] public JArray? Scroll { get; set; }
    [JsonProperty("targetPlanet")] public string? TargetPlanet { get; set; }
    [JsonProperty("scale")] public float? Scale { get; set; }
    [JsonProperty("easeParts")] public int? EaseParts { get; set; }
    [JsonProperty("easePartBehavior")] public string? EasePartBehavior { get; set; }
    [JsonProperty("enabled")] public bool? EffectEnabled { get; set; }
    [JsonProperty("threshold")] public float? Threshold { get; set; }
    [JsonProperty("intensity")] public float? Intensity { get; set; }
    [JsonProperty("strength")] public float? Strength { get; set; }
    [JsonProperty("fadeOut")] public bool? FadeOut { get; set; }
    [JsonProperty("color")] public string? Color { get; set; }
}
