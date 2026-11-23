using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Player.Engine.Events;

internal class AddImageDecoration : PreviewEvent
{
    [JsonIgnore] public int DecorationIndex { get; set; } = -1;
    [JsonProperty("decorationImage")] public string? Image { get; set; }
    [JsonProperty("visible")] public bool? Visible { get; set; }
    [JsonProperty("tag")] public string? Tag { get; set; }
    [JsonProperty("position")] public JArray? Position { get; set; }
    [JsonProperty("pivotOffset")] public JArray? Pivot { get; set; }
    [JsonProperty("relativeTo")] public string? RelativeTo { get; set; }
    [JsonProperty("rotation")] public float? Rotation { get; set; }
    [JsonProperty("scale")] public JArray? Scale { get; set; }
    [JsonProperty("originalSize")] public JArray? OriginalSize { get; set; }
    [JsonProperty("cropPivotOffset")] public JArray? CropPivotOffset { get; set; }
    [JsonProperty("scaleMultiplier")] public float? ScaleMultiplier { get; set; }
    [JsonProperty("tile")] public JArray? Tile { get; set; }
    [JsonProperty("color")] public string? Color { get; set; }
    [JsonProperty("opacity")] public float? Opacity { get; set; }
    [JsonProperty("depth")] public int? Depth { get; set; }
    [JsonProperty("syncFloorDepth")] public bool? SyncFloorDepth { get; set; }
    [JsonProperty("imageSmoothing")] public bool? Smoothing { get; set; }
    [JsonProperty("blendMode")] public string? BlendMode { get; set; }
    [JsonProperty("maskingType")] public string? MaskingType { get; set; }
    [JsonProperty("maskingTarget")] public string? MaskingTarget { get; set; }
    [JsonProperty("useMaskingDepth")] public bool? UseMaskingDepth { get; set; }
    [JsonProperty("maskingFrontDepth")] public int? MaskingFrontDepth { get; set; }
    [JsonProperty("maskingBackDepth")] public int? MaskingBackDepth { get; set; }
    [JsonProperty("parallax")] public JArray? Parallax { get; set; }
    [JsonProperty("parallaxOffset")] public JArray? ParallaxOffset { get; set; }
    [JsonProperty("stickToFloor")] public bool? StickToFloor { get; set; }
    [JsonProperty("lockRotation")] public bool? LockRotation { get; set; }
    [JsonProperty("lockScale")] public bool? LockScale { get; set; }
    [JsonProperty("hitbox")] public string? Hitbox { get; set; }
    [JsonProperty("failHitboxType")] public string? HitboxShape { get; set; }
    [JsonProperty("failHitboxScale")] public JArray? HitboxScale { get; set; }
    [JsonProperty("failHitboxOffset")] public JArray? HitboxOffset { get; set; }
    [JsonProperty("failHitboxRotation")] public float? HitboxRotation { get; set; }
    [JsonProperty("hitboxDetectTarget")] public string? HitboxDetectTarget { get; set; }
    [JsonProperty("hitboxTargetPlanet")] public string? HitboxTargetPlanet { get; set; }
    [JsonProperty("hitboxTriggerType")] public string? HitboxTriggerType { get; set; }
    [JsonProperty("hitboxRepeatInterval")] public float? HitboxRepeatInterval { get; set; }
    [JsonProperty("hitboxDecoTag")] public string? HitboxDecorationTag { get; set; }
    [JsonProperty("hitboxEventTag")] public string? HitboxEventTag { get; set; }
}

internal class MoveImageDecoration : TimedEvent
{
    private string? _image;
    [JsonProperty("tag")] public string? Tag { get; set; }
    [JsonProperty("visible")] public bool? Visible { get; set; }
    [JsonProperty("relativeTo")] public string? RelativeTo { get; set; }
    [JsonProperty("decorationImage", NullValueHandling = NullValueHandling.Include)]
    public string? Image
    {
        get => _image;
        set
        {
            _image = value;
            ImageSpecified = true;
        }
    }
    [JsonIgnore] public bool ImageSpecified { get; private set; }
    [JsonProperty("originalSize")] public JArray? OriginalSize { get; set; }
    [JsonProperty("cropPivotOffset")] public JArray? CropPivotOffset { get; set; }
    [JsonProperty("positionOffset")] public JArray? Position { get; set; }
    [JsonProperty("pivotOffset")] public JArray? Pivot { get; set; }
    [JsonProperty("rotationOffset")] public float? Rotation { get; set; }
    [JsonProperty("scale")] public JArray? Scale { get; set; }
    [JsonProperty("color")] public string? Color { get; set; }
    [JsonProperty("opacity")] public float? Opacity { get; set; }
    [JsonProperty("depth")] public int? Depth { get; set; }
    [JsonProperty("parallax")] public JArray? Parallax { get; set; }
    [JsonProperty("parallaxOffset")] public JArray? ParallaxOffset { get; set; }
    [JsonProperty("imageSmoothing")] public bool? Smoothing { get; set; }
    [JsonProperty("maskingType")] public string? MaskingType { get; set; }
    [JsonProperty("maskingTarget")] public string? MaskingTarget { get; set; }
    [JsonProperty("useMaskingDepth")] public bool? UseMaskingDepth { get; set; }
    [JsonProperty("maskingFrontDepth")] public int? MaskingFrontDepth { get; set; }
    [JsonProperty("maskingBackDepth")] public int? MaskingBackDepth { get; set; }
}

internal sealed class AddObjectDecoration : AddImageDecoration
{
    [JsonProperty("objectType")] public string? ObjectType { get; set; }
    [JsonProperty("trackType")] public string? TrackType { get; set; }
    [JsonProperty("trackAngle")] public float? TrackAngle { get; set; }
    [JsonProperty("trackStyle")] public string? TrackStyle { get; set; }
    [JsonProperty("trackColor")] public string? TrackColor { get; set; }
    [JsonProperty("trackOpacity")] public float? TrackOpacity { get; set; }
    [JsonProperty("planetColorType")] public string? PlanetColorType { get; set; }
    [JsonProperty("planetColor")] public string? PlanetColor { get; set; }
}

internal sealed class SetObjectDecoration : MoveImageDecoration
{
    [JsonProperty("trackColor")] public string? TrackColor { get; set; }
    [JsonProperty("trackOpacity")] public float? TrackOpacity { get; set; }
    [JsonProperty("planetColor")] public string? PlanetColor { get; set; }
}

internal sealed class AddParticleDecoration : AddImageDecoration
{
    [JsonExtensionData] public Dictionary<string, JToken> ParticleFields { get; set; } = [];
    [JsonProperty("color")] public JToken? ParticleColor { get; set; }
}

internal sealed class ParticleControlEvent : TimedEvent
{
    [JsonProperty("tag")] public string? Tag { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken> ParticleFields { get; set; } = new Dictionary<string, JToken>();
}

internal sealed class AddTextDecoration : AddImageDecoration
{
    [JsonProperty("decText")] public JToken? Text { get; set; }
    [JsonProperty("font")] public string? Font { get; set; }
}

internal sealed class SetTextDecoration : MoveImageDecoration
{
    [JsonProperty("decText")] public JToken? Text { get; set; }
}

internal sealed class ConditionalDecorationEvent : RuntimeEvent
{
    [JsonProperty("perfectTag")] public string? Perfect { get; set; }
    [JsonProperty("earlyPerfectTag")] public string? EarlyPerfect { get; set; }
    [JsonProperty("latePerfectTag")] public string? LatePerfect { get; set; }
    [JsonProperty("veryEarlyTag")] public string? VeryEarly { get; set; }
    [JsonProperty("veryLateTag")] public string? VeryLate { get; set; }
    [JsonProperty("tooEarlyTag")] public string? TooEarly { get; set; }
    [JsonProperty("tooLateTag")] public string? TooLate { get; set; }
    [JsonProperty("lossTag")] public string? Loss { get; set; }
    [JsonProperty("onCheckpointTag")] public string? Checkpoint { get; set; }
    [JsonProperty("hitTag")] public string? Hit { get; set; }
    [JsonProperty("barelyTag")] public string? Barely { get; set; }
    [JsonProperty("missTag")] public string? Miss { get; set; }

    public string?[] Tags() =>
        [Valid(Perfect), Valid(EarlyPerfect) ?? Valid(Hit), Valid(LatePerfect) ?? Valid(Hit),
         Valid(VeryEarly) ?? Valid(Barely), Valid(VeryLate) ?? Valid(Barely),
         Valid(TooEarly) ?? Valid(Miss), Valid(TooLate) ?? Valid(Miss), Valid(Loss), Valid(Checkpoint)];

    private static string? Valid(string? tag) => string.IsNullOrWhiteSpace(tag) ||
        tag is "NONE" or "无" or "無" or "AUCUN" or "ŽÁDNÝ" or "NICHTS" ? null : tag;
}
