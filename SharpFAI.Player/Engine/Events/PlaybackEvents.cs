using Newtonsoft.Json;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine.Events;

internal sealed class PauseAngleEvent : PreviewEvent
{
    [JsonProperty("countdownTicks")]
    public int? CountdownTicks { get; set; }

    [JsonProperty("angleCorrectionDir")]
    public EventEnums.AngleCorrectionDirection? AngleCorrectionDir { get; set; }
}

internal sealed class ScaleMarginEvent : PreviewEvent
{
    [JsonProperty("scale")]
    public double? Scale { get; set; }
}

internal sealed class AutoPlayTilesEvent : PreviewEvent
{
    [JsonProperty("enabled")]
    public bool? Auto { get; set; }

    [JsonProperty("safetyTiles")]
    public bool? Safe { get; set; }
}

internal sealed class MultitapEvent : PreviewEvent
{
    [JsonProperty("taps")]
    public int? Taps { get; set; }
}

internal sealed class CheckpointEvent : PreviewEvent
{
    [JsonProperty("tileOffset")]
    public int? TileOffset { get; set; }
}

internal sealed class MoveCameraEvent : TimedEvent
{
    [JsonProperty("relativeTo")]
    public EventEnums.CamMovementType? RelativeTo { get; set; }

    [JsonProperty("position")]
    public EventVector? Position { get; set; }

    [JsonProperty("rotation")]
    public float? Rotation { get; set; }

    [JsonProperty("zoom")]
    public float? Zoom { get; set; }

    [JsonProperty("minVfxOnly")]
    public bool? MinVfxOnly { get; set; }
}
