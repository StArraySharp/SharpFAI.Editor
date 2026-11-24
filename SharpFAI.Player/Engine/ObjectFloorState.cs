using OpenTK.Mathematics;
using SharpFAI.Player.Engine.Events;

namespace SharpFAI.Player.Engine;

internal enum ObjectFloorIcon
{
    None, Snail, DoubleSnail, Rabbit, DoubleRabbit, Swirl, Checkpoint, HoldArrowShort, HoldArrowLong,
    HoldReleaseShort, HoldReleaseLong, MultiPlanetTwo, MultiPlanetThreeMore, Portal,
}

internal sealed record ObjectFloorState
{
    public const string DefaultColor = "debb7b";
    public TrackStyleInfo Style { get; init; } = TrackStyleInfo.Standard;
    public float Angle { get; init; } = 180f;
    public bool Midspin { get; init; }
    public TrackColorMode ColorMode { get; init; }
    public Vector4 SecondaryColor { get; init; } = Vector4.One;
    public float ColorPeriod { get; init; } = 2f;
    public ObjectFloorIcon Icon { get; init; }
    public float IconAngle { get; init; }
    public bool IconFlipped { get; init; }
    public bool RedSwirl { get; init; }
    public bool GraySpeedIcon { get; init; }
    public float SpeedIconBpm { get; init; } = 100f;
    public bool IconOutlines { get; init; }
    public bool GlowEnabled { get; init; }
    public Vector4 GlowColor { get; init; } = Vector4.One;

    public static ObjectFloorState From(AddObjectDecoration source)
    {
        bool midspin = source.TrackType == "1" ||
            string.Equals(source.TrackType, "Midspin", StringComparison.OrdinalIgnoreCase);
        var state = new ObjectFloorState
        {
            Angle = midspin ? 360f : 180f,
            Midspin = midspin,
            SpeedIconBpm = Finite(source.TrackSetSpeedIconBpm) ?? 100f,
        };
        var change = new Change(source, source);
        return change.Sample(state, state, 1f, useAngle: !midspin);
    }

    public Vector4 EvaluateColor(Vector4 primary, double time, float volume) =>
        new FloorTrackState(Style, primary, SecondaryColor, ColorMode, 0, ColorPeriod, 0, -1, 0, 0)
            .EvaluateColor(0, (float)time, volume);

    public static Vector4 ReadTint(AddObjectDecoration source) =>
        FloorTrackStateResolver.ParseColor(source.TrackColor ?? DefaultColor) ?? Vector4.One;

    public static float ReadOpacity(AddObjectDecoration source) =>
        Math.Clamp((source.TrackOpacity ?? 100f) * .01f, 0f, 1f);

    private static float? Finite(float? value) => value is float number && float.IsFinite(number) ? number : null;

    internal sealed class Change(PlaybackEvent evt, IFloorDecorationProperties fields)
    {
        private readonly TrackStyleInfo? _style = evt.Enabled("trackStyle", fields.TrackStyle) is { Length: > 0 } style
            ? TrackStyleInfo.Parse(style) : null;
        private readonly float? _angle = Finite(evt.Enabled("trackAngle", fields.TrackAngle));
        private readonly TrackColorMode? _colorMode = evt.Enabled("trackColorType", fields.TrackColorType) switch
        {
            "Single" or "0" => TrackColorMode.Single,
            "Glow" or "1" => TrackColorMode.Glow,
            "Blink" or "2" => TrackColorMode.Blink,
            "Switch" or "3" => TrackColorMode.Switch,
            "Rainbow" or "4" => TrackColorMode.Rainbow,
            "Volume" or "5" => TrackColorMode.Volume,
            _ => null,
        };
        private readonly Vector4? _secondary = FloorTrackStateResolver.ParseColor(
            evt.Enabled("secondaryTrackColor", fields.SecondaryTrackColor));
        private readonly float? _period = Finite(evt.Enabled("trackColorAnimDuration", fields.TrackColorAnimDuration));
        private readonly ObjectFloorIcon? _icon = Enum.TryParse(evt.Enabled("trackIcon", fields.TrackIcon),
            out ObjectFloorIcon icon) && Enum.IsDefined(icon) ? icon : null;
        private readonly float? _iconAngle = Finite(evt.Enabled("trackIconAngle", fields.TrackIconAngle));
        private readonly bool? _iconFlipped = evt.Enabled("trackIconFlipped", fields.TrackIconFlipped);
        private readonly bool? _redSwirl = evt.Enabled("trackRedSwirl", fields.TrackRedSwirl);
        private readonly bool? _graySpeed = evt.Enabled("trackGraySetSpeedIcon", fields.TrackGraySetSpeedIcon);
        private readonly bool? _outlines = evt.Enabled("trackIconOutlines", fields.TrackIconOutlines);
        private readonly bool? _glowEnabled = evt.Enabled("trackGlowEnabled", fields.TrackGlowEnabled);
        private readonly Vector4? _glowColor = FloorTrackStateResolver.ParseColor(
            evt.Enabled("trackGlowColor", fields.TrackGlowColor));
        private readonly bool _resetSpeed = evt is SetObjectDecoration;

        public ObjectFloorState Sample(ObjectFloorState current, ObjectFloorState from, float p, bool useAngle = true)
        {
            float angle = useAngle && _angle is float turn ? Mix(from.Angle, turn, p) : current.Angle;
            return current with
            {
                Style = _style ?? current.Style,
                Angle = angle,
                ColorMode = _colorMode ?? current.ColorMode,
                SecondaryColor = _secondary ?? current.SecondaryColor,
                ColorPeriod = _period ?? current.ColorPeriod,
                Icon = _icon ?? current.Icon,
                IconAngle = _iconAngle is float rotation ? Mix(from.IconAngle, rotation, p) : current.IconAngle,
                IconFlipped = _iconFlipped ?? current.IconFlipped,
                RedSwirl = _redSwirl ?? current.RedSwirl,
                GraySpeedIcon = _graySpeed ?? current.GraySpeedIcon,
                SpeedIconBpm = _resetSpeed && (_icon.HasValue || _graySpeed.HasValue || _redSwirl.HasValue)
                    ? 100f : current.SpeedIconBpm,
                IconOutlines = _outlines ?? current.IconOutlines,
                GlowEnabled = _glowEnabled ?? current.GlowEnabled,
                GlowColor = _glowColor is Vector4 color ? Vector4.Lerp(from.GlowColor, color, p) : current.GlowColor,
            };
        }

        private static float Mix(float from, float to, float progress) => from + (to - from) * progress;

    }
}
