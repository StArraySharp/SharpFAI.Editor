using Newtonsoft.Json;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine.Events;

internal abstract class PlaybackEvent
{
    [JsonIgnore]
    public EventType EventType { get; internal set; }

    [JsonProperty("floor")]
    public int Floor { get; set; }

    [JsonProperty("active")]
    public bool? Active { get; set; }

    [JsonProperty("disabled")]
    public Dictionary<string, bool>? Disabled { get; set; }

    [JsonIgnore]
    public BaseEvent? Source { get; internal set; }

    [JsonIgnore]
    public int SourceIndex { get; internal set; } = -1;

    public bool IsActive => Active != false;

    internal PlaybackEvent WithFloor(int floor)
    {
        if (Floor == floor) return this;
        var copy = (PlaybackEvent)MemberwiseClone();
        copy.Floor = floor;
        return copy;
    }

    public T Enabled<T>(string property, T value) => Disabled?.TryGetValue(property, out bool disabled) == true && disabled
        ? default! : value;
}

/// <summary>Resolves authored state visible before playback.</summary>
internal abstract class PreviewEvent : PlaybackEvent;

/// <summary>Applies effects after playback reaches their trigger.</summary>
internal abstract class RuntimeEvent : PlaybackEvent;

internal abstract class TimedEvent : RuntimeEvent
{
    [JsonProperty("angleOffset")]
    public float? AngleOffset { get; set; }

    [JsonProperty("duration")]
    public float? Duration { get; set; }

    [JsonProperty("ease")]
    public EventEnums.Ease? Ease { get; set; }

    [JsonProperty("eventTag")]
    public string? EventTag { get; set; }
}
