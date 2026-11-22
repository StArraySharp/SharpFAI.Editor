using Newtonsoft.Json;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine.Events;

internal sealed class PositionTrack : PreviewEvent
{
    [JsonProperty("positionOffset")]
    public EventVector? PositionOffset { get; set; }

    [JsonProperty("relativeTo")]
    public TileReference? RelativeTo { get; set; }

    [JsonProperty("rotation")]
    public float? Rotation { get; set; }

    [JsonProperty("scale")]
    public EventVector? Scale { get; set; }

    [JsonProperty("opacity")]
    public float? Opacity { get; set; }

    [JsonProperty("justThisTile")]
    public bool? JustThisTile { get; set; }

    [JsonProperty("editorOnly")]
    public bool? EditorOnly { get; set; }

    [JsonProperty("stickToFloors")]
    public bool? StickToFloors { get; set; }
}

internal sealed class ScaleRadius : PreviewEvent
{
    [JsonProperty("scale")]
    public float? Scale { get; set; }
}

internal sealed class HoldTrack : PreviewEvent
{
    [JsonProperty("distanceMultiplier")]
    public float? DistanceMultiplier { get; set; }

    [JsonProperty("landingAnimation")]
    public bool? LandingAnimation { get; set; }
}

/// <summary>Appearance properties shared by preview color changes and runtime recoloring.</summary>
internal interface ITrackColorProperties
{
    string? TrackColor { get; }
    string? SecondaryTrackColor { get; }
    string? TrackColorType { get; }
    float? TrackColorAnimDuration { get; }
    string? TrackColorPulse { get; }
    float? TrackPulseLength { get; }
    string? TrackStyle { get; }
    string? TrackTexture { get; }
    float? TrackTextureScale { get; }
    string? TrackShadowColor { get; }
    float? TrackGlowIntensity { get; }
    bool? FloorIconOutlines { get; }
    float? BeatsBehind { get; }
}

/// <summary>Changes the authored appearance of the track.</summary>
internal class ColorTrack : PreviewEvent, ITrackColorProperties
{
    public ColorTrack() => EventType = EventType.ColorTrack;

    [JsonProperty("justThisTile", NullValueHandling = NullValueHandling.Ignore)]
    public bool? JustThisTile { get; set; }

    [JsonProperty("trackColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackColor { get; set; }

    [JsonProperty("secondaryTrackColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? SecondaryTrackColor { get; set; }

    [JsonProperty("trackColorType", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackColorType { get; set; }

    [JsonProperty("trackColorAnimDuration", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackColorAnimDuration { get; set; }

    [JsonProperty("trackColorPulse", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackColorPulse { get; set; }

    [JsonProperty("trackPulseLength", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackPulseLength { get; set; }

    [JsonProperty("trackStyle", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackStyle { get; set; }

    [JsonProperty("trackTexture", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackTexture { get; set; }

    [JsonProperty("trackTextureScale", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackTextureScale { get; set; }

    [JsonProperty("trackShadowColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackShadowColor { get; set; }

    [JsonProperty("trackGlowIntensity", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackGlowIntensity { get; set; }

    [JsonProperty("floorIconOutlines", NullValueHandling = NullValueHandling.Ignore)]
    public bool? FloorIconOutlines { get; set; }

    [JsonProperty("beatsBehind", NullValueHandling = NullValueHandling.Ignore)]
    public float? BeatsBehind { get; set; }
}

/// <summary>Legacy combined color and animation event.</summary>
internal sealed class ChangeTrack : ColorTrack
{
    public ChangeTrack() => EventType = EventType.ChangeTrack;

    [JsonProperty("trackAnimation", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackAnimation { get; set; }

    [JsonProperty("trackDisappearAnimation", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackDisappearAnimation { get; set; }

    [JsonProperty("beatsAhead", NullValueHandling = NullValueHandling.Ignore)]
    public float? BeatsAhead { get; set; }
}

/// <summary>Configures appearance and disappearance effects for the runtime track.</summary>
internal sealed class AnimateTrack : RuntimeEvent
{
    public AnimateTrack() => EventType = EventType.AnimateTrack;

    [JsonProperty("trackAnimation", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackAnimation { get; set; }

    [JsonProperty("trackDisappearAnimation", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackDisappearAnimation { get; set; }

    [JsonProperty("beatsAhead", NullValueHandling = NullValueHandling.Ignore)]
    public float? BeatsAhead { get; set; }

    [JsonProperty("beatsBehind", NullValueHandling = NullValueHandling.Ignore)]
    public float? BeatsBehind { get; set; }

    [JsonProperty("justThisTile", NullValueHandling = NullValueHandling.Ignore)]
    public bool? JustThisTile { get; set; }
}

/// <summary>Properties shared by runtime events targeting a range of floors.</summary>
internal abstract class TrackRangeEvent : TimedEvent
{
    [JsonProperty("startTile", NullValueHandling = NullValueHandling.Ignore)]
    public TileReference? StartTile { get; set; }

    [JsonProperty("endTile", NullValueHandling = NullValueHandling.Ignore)]
    public TileReference? EndTile { get; set; }

    [JsonProperty("gapLength", NullValueHandling = NullValueHandling.Ignore)]
    public int? GapLength { get; set; }

    [JsonProperty("maxVfxOnly", NullValueHandling = NullValueHandling.Ignore)]
    public bool? MaxVfxOnly { get; set; }
}

/// <summary>Moves, rotates, scales, or fades floors on the playback timeline.</summary>
internal sealed class MoveTrack : TrackRangeEvent
{
    public MoveTrack() => EventType = EventType.MoveTrack;

    [JsonProperty("positionOffset", NullValueHandling = NullValueHandling.Ignore)]
    public EventVector? PositionOffset { get; set; }

    [JsonProperty("rotationOffset", NullValueHandling = NullValueHandling.Ignore)]
    public float? RotationOffset { get; set; }

    [JsonProperty("scale", NullValueHandling = NullValueHandling.Ignore)]
    public EventVector? Scale { get; set; }

    [JsonProperty("opacity", NullValueHandling = NullValueHandling.Ignore)]
    public float? Opacity { get; set; }
}

/// <summary>Changes the appearance of a range of floors at runtime.</summary>
internal sealed class RecolorTrack : TrackRangeEvent, ITrackColorProperties
{
    public RecolorTrack() => EventType = EventType.RecolorTrack;

    [JsonProperty("trackColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackColor { get; set; }

    [JsonProperty("secondaryTrackColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? SecondaryTrackColor { get; set; }

    [JsonProperty("trackColorType", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackColorType { get; set; }

    [JsonProperty("trackColorAnimDuration", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackColorAnimDuration { get; set; }

    [JsonProperty("trackColorPulse", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackColorPulse { get; set; }

    [JsonProperty("trackPulseLength", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackPulseLength { get; set; }

    [JsonProperty("trackStyle", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackStyle { get; set; }

    [JsonProperty("trackTexture", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackTexture { get; set; }

    [JsonProperty("trackTextureScale", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackTextureScale { get; set; }

    [JsonProperty("trackShadowColor", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackShadowColor { get; set; }

    [JsonProperty("trackGlowIntensity", NullValueHandling = NullValueHandling.Ignore)]
    public float? TrackGlowIntensity { get; set; }

    [JsonProperty("floorIconOutlines", NullValueHandling = NullValueHandling.Ignore)]
    public bool? FloorIconOutlines { get; set; }

    [JsonProperty("beatsBehind", NullValueHandling = NullValueHandling.Ignore)]
    public float? BeatsBehind { get; set; }
}

/// <summary>Changes the authored dimensions of floors.</summary>
internal sealed class TileDimensions : PreviewEvent
{
    public TileDimensions() => EventType = EventType.TileDimensions;

    [JsonProperty("width", NullValueHandling = NullValueHandling.Ignore)]
    public float? Width { get; set; }

    [JsonProperty("length", NullValueHandling = NullValueHandling.Ignore)]
    public float? Length { get; set; }

    [JsonProperty("justThisTile", NullValueHandling = NullValueHandling.Ignore)]
    public bool? JustThisTile { get; set; }
}

/// <summary>Controls floor and judgement visibility.</summary>
internal sealed class Hide : PreviewEvent
{
    public Hide() => EventType = EventType.Hide;

    [JsonProperty("hideTileIcon", NullValueHandling = NullValueHandling.Ignore)]
    public bool? HideTileIcon { get; set; }

    [JsonProperty("hideJudgment", NullValueHandling = NullValueHandling.Ignore)]
    public bool? HideJudgment { get; set; }

    [JsonProperty("hideTrack", NullValueHandling = NullValueHandling.Ignore)]
    public bool? HideTrack { get; set; }

    [JsonProperty("justThisTile", NullValueHandling = NullValueHandling.Ignore)]
    public bool? JustThisTile { get; set; }
}

/// <summary>Overrides the icon on one floor.</summary>
internal sealed class SetFloorIcon : PreviewEvent
{
    public SetFloorIcon() => EventType = EventType.SetFloorIcon;

    [JsonProperty("icon", NullValueHandling = NullValueHandling.Ignore)]
    public string? Icon { get; set; }

    [JsonProperty("floorIcon", NullValueHandling = NullValueHandling.Ignore)]
    public string? FloorIcon { get; set; }
}

/// <summary>Repeats tagged runtime events on a beat or floor interval.</summary>
internal sealed class RepeatEvents : RuntimeEvent
{
    public RepeatEvents() => EventType = EventType.RepeatEvents;

    [JsonProperty("repeatType", NullValueHandling = NullValueHandling.Ignore)]
    public string? RepeatType { get; set; }

    [JsonProperty("repetitions", NullValueHandling = NullValueHandling.Ignore)]
    public int? Repetitions { get; set; }

    [JsonProperty("floorCount", NullValueHandling = NullValueHandling.Ignore)]
    public int? FloorCount { get; set; }

    [JsonProperty("interval", NullValueHandling = NullValueHandling.Ignore)]
    public float? Interval { get; set; }

    [JsonProperty("gapLength", NullValueHandling = NullValueHandling.Ignore)]
    public int? GapLength { get; set; }

    [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
    public string? Tag { get; set; }

    [JsonProperty("executeOnCurrentFloor", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ExecuteOnCurrentFloor { get; set; }
}
