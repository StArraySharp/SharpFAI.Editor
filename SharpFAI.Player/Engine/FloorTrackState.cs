using SharpFAI.Player.Engine.Events;
using OpenTK.Mathematics;
using SharpFAI.Serialization;
using SharpFAI.Framework;

namespace SharpFAI.Player.Engine;

/// <summary>
/// How a tile's colour animates once the chart has been resolved. Values match the game's
/// <c>TrackColorType</c> enum.
/// </summary>
public enum TrackColorMode
{
    /// <summary>A single flat colour (<c>trackColor</c>).</summary>
    Single = 0,

    /// <summary>Alternates between two colours per floor index.</summary>
    Stripes = 1,

    /// <summary>Cosine ping-pong between two colours.</summary>
    Glow = 2,

    /// <summary>Linear ramp looping from the first colour to the second.</summary>
    Blink = 3,

    /// <summary>Hard flip at half the animation duration.</summary>
    Switch = 4,

    /// <summary>Hue sweep, keeping saturation and value from the first colour.</summary>
    Rainbow = 5,

    /// <summary>Blends the two colours with the sampled audio level.</summary>
    Volume = 6,
}

/// <summary>
/// The per-floor track appearance after <c>ColorTrack</c> events have been resolved: which style
/// the tile is drawn with, the two colours it animates between, and the animation parameters.
/// </summary>
/// <remarks>
/// The game keeps this state on <c>scrFloor</c> (as <c>specialColor*</c>) and in the closure of
/// <c>scnGame.CreateFloor</c>. This record is the same information in one immutable value, so a
/// floor is fully described without reaching back into the level.
/// </remarks>
public readonly record struct FloorTrackState(
    TrackStyleInfo Style,
    Vector4 Color1,
    Vector4 Color2,
    TrackColorMode Mode,
    int PulseMode,
    float AnimDuration,
    float PulseLength,
    int StartOfColorChange,
    float GlowMultiplier,
    float BeatsBehind)
{
    public bool IconOutline { get; init; }
    public Vector4? ShadowColor { get; init; }
    internal Vector4? BaseColor1 { get; init; }
    internal string? TextureName { get; init; }
    internal float TextureScale { get; init; } = 1f;
    internal FloorRenderTransform Transform { get; init; } = new(Vector2.Zero, Vector2.One, 0f);
    internal float Opacity { get; init; } = 1f;
    internal bool StickToFloors { get; init; } = true;
    internal Vector2 Dimensions { get; init; } = Vector2.One;
    internal float LengthRatio { get; init; } = 1f;
    internal FloorIcon? Icon { get; init; }
    internal bool ReverseIcon { get; init; }
    internal bool HideIcon { get; init; }
    internal bool HideJudgment { get; init; }
    internal bool Hidden { get; init; }
    internal TrackAnimation Appear { get; init; }
    internal TrackAnimation Disappear { get; init; }
    internal float BeatsAhead { get; init; }
    internal double AnimationReferenceBpm { get; init; }
    internal double LastAnimateTrackBpm { get; init; }

    internal FloorMaterial Material
    {
        get
        {
            string? texture = string.IsNullOrWhiteSpace(TextureName) ? null : TextureName;
            float scale = texture is not null && Math.Abs(TextureScale) >= 1e-6f ? TextureScale : 0f;
            return new FloorMaterial(Style, texture, scale, ShadowColor, IconOutline,
                HideIcon, Icon, ReverseIcon, Dimensions, LengthRatio);
        }
    }

    internal FloorTrackState ApplyColorFields(PlaybackEvent item, ITrackColorProperties color, int anchor)
    {
        string? mode = item.Enabled("trackColorType", color.TrackColorType);
        string? pulse = item.Enabled("trackColorPulse", color.TrackColorPulse);
        float? glow = item.Enabled("trackGlowIntensity", color.TrackGlowIntensity);
        return this with
        {
            Style = item.Enabled("trackStyle", color.TrackStyle) is { Length: > 0 } style
                ? TrackStyleInfo.Parse(style) : Style,
            Color1 = FloorTrackStateResolver.ParseColor(item.Enabled("trackColor", color.TrackColor))
                ?? BaseColor1 ?? Color1,
            BaseColor1 = null,
            Color2 = FloorTrackStateResolver.ParseColor(item.Enabled("secondaryTrackColor", color.SecondaryTrackColor))
                ?? Color2,
            Mode = mode is null ? Mode : FloorTrackStateResolver.ParseColorMode(mode),
            PulseMode = pulse is null ? PulseMode : FloorTrackStateResolver.ParsePulseMode(pulse),
            AnimDuration = item.Enabled("trackColorAnimDuration", color.TrackColorAnimDuration) ?? AnimDuration,
            PulseLength = item.Enabled("trackPulseLength", color.TrackPulseLength) ?? PulseLength,
            StartOfColorChange = mode is null ? StartOfColorChange : anchor,
            GlowMultiplier = glow is float intensity ? intensity / 100f : GlowMultiplier,
            BeatsBehind = item.Enabled("beatsBehind", color.BeatsBehind) ?? BeatsBehind,
            IconOutline = item.Enabled("floorIconOutlines", color.FloorIconOutlines) ?? IconOutline,
            ShadowColor = FloorTrackStateResolver.ParseColor(item.Enabled("trackShadowColor", color.TrackShadowColor))
                ?? ShadowColor,
            TextureName = item.Enabled("trackTexture", color.TrackTexture) ?? TextureName,
            TextureScale = item.Enabled("trackTextureScale", color.TrackTextureScale) ?? TextureScale,
        };
    }

    internal FloorTrackState ApplyAnimationFields(
        PlaybackEvent item, string? appearance, string? disappearance, float? ahead, float? behind) => this with
    {
        Appear = item.Enabled("trackAnimation", appearance) is { } appear
            ? FloorTrackStateResolver.ParseAnimation(appear) : Appear,
        Disappear = item.Enabled("trackDisappearAnimation", disappearance) is { } disappear
            ? FloorTrackStateResolver.ParseAnimation(disappear) : Disappear,
        BeatsAhead = item.Enabled("beatsAhead", ahead) ?? BeatsAhead,
        BeatsBehind = item.Enabled("beatsBehind", behind) ?? BeatsBehind,
    };

    internal FloorTrackState WithDisplayColor(int floor)
    {
        Vector4 primary = BaseColor1 ?? Color1;
        Vector4 display = Mode switch
        {
            TrackColorMode.Stripes when (floor - StartOfColorChange) % 2 != 0 => Color2,
            TrackColorMode.Rainbow => new Vector4(1f, 1f, 1f, primary.W),
            _ => primary,
        };
        return this with { BaseColor1 = primary, Color1 = display };
    }


    /// <summary>
    /// Phase offset this floor's animation starts at, in the same units as
    /// <c>scrFloor.specialTimeOffset</c>. <c>PulseMode</c> is 0 for None, 1 for Forward and 2 for
    /// Backward, matching the game's <c>TrackColorPulse</c>.
    /// </summary>
    public float TimeOffset(int floorIndex)
    {
        if (PulseLength <= 0f)
        {
            return 0f;
        }

        if (PulseMode == 1)
        {
            // Forward: (1 - 1 / pulseLength * (seqID % pulseLength)) * duration
            return (1f - 1f / PulseLength * (floorIndex % PulseLength)) * AnimDuration;
        }

        if (PulseMode == 2)
        {
            // Backward: 1 / pulseLength * (seqID % pulseLength) * duration
            return 1f / PulseLength * (floorIndex % PulseLength) * AnimDuration;
        }

        return 0f;
    }

    /// <summary>
    /// The colour this floor shows at <paramref name="time"/> seconds into playback, replicating
    /// <c>scrFloor.SpecialUpdate</c>. <c>Stripes</c> and <c>Single</c> are static and return
    /// <see cref="Color1"/>: the game picks the stripe colour while baking and never animates it.
    /// </summary>
    public Vector4 EvaluateColor(int floorIndex, float time, float volume = 0f)
    {
        // The game guards on `type != Single || type != Stripes`, which is always true, so the switch
        // below runs for every mode.
        if (Mode is TrackColorMode.Single or TrackColorMode.Stripes)
        {
            return Color1;
        }

        if (Mode == TrackColorMode.Volume)
        {
            return Vector4.Lerp(Color1, Color2, Math.Clamp(volume, 0f, 1f));
        }

        if (AnimDuration <= 1e-6f)
        {
            // A zero duration would divide by zero; the game's DOTween clamps it away too
            return Color1;
        }

        float stamp = time + TimeOffset(floorIndex);

        switch (Mode)
        {
            case TrackColorMode.Glow:
            {
                float t = (1f - MathF.Cos(MathF.PI * 2f * (stamp / AnimDuration))) / 2f;
                return Vector4.Lerp(Color1, Color2, t);
            }

            case TrackColorMode.Rainbow:
            {
                ColorRgbToHsv(BaseColor1 ?? Color1, out _, out float saturation, out float value);
                return ColorHsvToRgb(stamp / AnimDuration % 1f, saturation, value) with
                {
                    W = Color1.W,
                };
            }

            case TrackColorMode.Blink:
            {
                // DOVirtual.EasedValue(0, 1, x, Ease.Linear) is plain linear interpolation
                float t = stamp % AnimDuration / AnimDuration;
                return Vector4.Lerp(Color1, Color2, t);
            }

            case TrackColorMode.Switch:
                return stamp % AnimDuration < AnimDuration / 2f ? Color1 : Color2;

            default:
                return Color1;
        }
    }

    /// <summary>RGB to HSV with components in 0..1, matching <c>Color.RGBToHSV</c>.</summary>
    private static void ColorRgbToHsv(Vector4 color, out float hue, out float saturation, out float value)
    {
        float max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        float min = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        value = max;
        float delta = max - min;
        saturation = max <= 0f ? 0f : delta / max;

        if (delta <= 0f)
        {
            hue = 0f;
            return;
        }

        if (max == color.X)
        {
            hue = (color.Y - color.Z) / delta % 6f;
        }
        else if (max == color.Y)
        {
            hue = ((color.Z - color.X) / delta) + 2f;
        }
        else
        {
            hue = ((color.X - color.Y) / delta) + 4f;
        }

        hue /= 6f;

        if (hue < 0f)
        {
            hue += 1f;
        }
    }

    /// <summary>HSV to RGB with components in 0..1, matching <c>Color.HSVToRGB</c>.</summary>
    private static Vector4 ColorHsvToRgb(float hue, float saturation, float value)
    {
        if (saturation <= 0f)
        {
            return new Vector4(value, value, value, 1f);
        }

        float scaled = hue % 1f;

        if (scaled < 0f)
        {
            scaled += 1f;
        }

        scaled *= 6f;

        int sector = (int)MathF.Floor(scaled);
        float fraction = scaled - sector;

        float p = value * (1f - saturation);
        float q = value * (1f - saturation * fraction);
        float t = value * (1f - saturation * (1f - fraction));

        return (sector % 6) switch
        {
            0 => new Vector4(value, t, p, 1f),
            1 => new Vector4(q, value, p, 1f),
            2 => new Vector4(p, value, t, 1f),
            3 => new Vector4(p, q, value, 1f),
            4 => new Vector4(t, p, value, 1f),
            _ => new Vector4(value, p, q, 1f),
        };
    }
}

/// <summary>Resolves preview-time track events in floor order.</summary>
public static class FloorTrackStateResolver
{
    public const string DefaultStyle = "Standard";

    public static FloorTrackState[] Resolve(Level level, IReadOnlyList<SharpFAI.Framework.Floor> floors) =>
        Resolve(level, floors, includeEditorOnly: true);

    internal static FloorTrackState[] Resolve(
        Level level, IReadOnlyList<SharpFAI.Framework.Floor> floors, bool includeEditorOnly) =>
        Resolve(level, floors, PlaybackEventBinder.Bind(level), includeEditorOnly);

    internal static FloorTrackState[] Resolve(Level level, IReadOnlyList<SharpFAI.Framework.Floor> floors,
        IReadOnlyList<PlaybackEvent> events, bool includeEditorOnly, ReadOnlySpan<double> floorBpms = default)
    {
        FloorTrackState[] result = new FloorTrackState[floors.Count];
        FloorTrackState live = ReadDefaults(level);
        ILookup<int, PlaybackEvent> byFloor = events
            .Where(item => TrackPreview.Applies(item, includeEditorOnly))
            .ToLookup(item => item.Floor);

        for (int i = 0; i < floors.Count; i++)
        {
            FloorTrackState current = live;
            double floorBpm = floorBpms.IsEmpty ? floors[i].bpm : floorBpms[i];
            TrackPreviewContext context = new(floors, result, i, Floor.length * 2f * live.LengthRatio, floorBpm);
            foreach (PlaybackEvent trackEvent in byFloor[i])
            {
                context = context with { ContinuingOffset = live.Transform.Offset };
                current = TrackPreview.Apply(trackEvent, current, context);
                if (!TrackPreview.ThisFloorOnly(trackEvent))
                {
                    live = TrackPreview.Apply(trackEvent, live, context);
                    if (trackEvent is PositionTrack position && position.Enabled("positionOffset", position.PositionOffset) is not null)
                    {
                        live = live with { Transform = live.Transform with { Offset = current.Transform.Offset } };
                    }
                }
            }

            result[i] = current.WithDisplayColor(i);
        }

        return result;
    }

    private static FloorTrackState ReadDefaults(Level level)
    {
        T Read<T>(string key, T fallback) => LevelPlaybackSettings.Setting(level, key, fallback);

        string? disappear = Read<string?>("trackDisappearAnimation", null);
        double bpm = Read("bpm", 120.0);
        if (!double.IsFinite(bpm) || bpm <= 1e-6)
        {
            bpm = 120.0;
        }
        return new FloorTrackState(
            TrackStyleInfo.Parse(Read("trackStyle", DefaultStyle)),
            ParseColor(Read<string?>("trackColor", null)) ?? Vector4.One,
            ParseColor(Read<string?>("secondaryTrackColor", null)) ?? Vector4.One,
            ParseColorMode(Read<string?>("trackColorType", null)),
            ParsePulseMode(Read<string?>("trackColorPulse", null)),
            Read("trackColorAnimDuration", 2f),
            Read("trackPulseLength", 10f),
            0,
            Read("trackGlowIntensity", 0f) / 100f,
            Read("beatsBehind", 4f))
        {
            IconOutline = Read("floorIconOutlines", false),
            StickToFloors = Read("stickToFloors", true),
            LengthRatio = Read("tileShape", "Long") == "Short" ? 2f / 3f : 1f,
            ShadowColor = ParseColor(Read<string?>("trackShadowColor", null)),
            TextureName = Read<string?>("trackTexture", null),
            TextureScale = Read("trackTextureScale", 1f),
            Appear = ParseAnimation(Read<string?>("trackAnimation", null)),
            Disappear = ParseAnimation(disappear),
            BeatsAhead = Read("beatsAhead", 3f),
            AnimationReferenceBpm = bpm,
            LastAnimateTrackBpm = bpm,
        };
    }

    internal static TrackColorMode ParseColorMode(string? name) =>
        Enum.TryParse(name, true, out TrackColorMode mode) ? mode : TrackColorMode.Single;

    internal static int ParsePulseMode(string? name) => name switch
    {
        "Forward" => 1,
        "Backward" => 2,
        _ => 0,
    };

    internal static TrackAnimation ParseAnimation(string? name) =>
        Enum.TryParse(name, true, out TrackAnimation mode) ? mode : TrackAnimation.None;

    /// <summary>Parse an ADOFAI <c>RRGGBB</c> / <c>RRGGBBAA</c> colour; alpha sits last.</summary>
    public static Vector4? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        string text = hex.Trim().TrimStart('#');

        if (text.Length != 6 && text.Length != 8)
        {
            return null;
        }

        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint value))
        {
            return null;
        }

        if (text.Length == 6)
        {
            return new Vector4(
                ((value >> 16) & 0xFF) / 255f,
                ((value >> 8) & 0xFF) / 255f,
                (value & 0xFF) / 255f,
                1f);
        }

        return new Vector4(
            ((value >> 24) & 0xFF) / 255f,
            ((value >> 16) & 0xFF) / 255f,
            ((value >> 8) & 0xFF) / 255f,
            (value & 0xFF) / 255f);
    }

}
