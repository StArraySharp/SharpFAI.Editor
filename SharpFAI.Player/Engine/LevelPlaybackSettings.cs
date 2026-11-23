using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

internal enum BackgroundDisplay { FitToScreen, Unscaled, Tiled }

internal sealed record LevelPlaybackSettings
{
    public static readonly Vector4 DefaultBackground = new(.09f, .09f, .11f, 1f);

    public Vector4 Background { get; init; } = DefaultBackground;
    public string? BackgroundImage { get; init; }
    public Vector4 ImageTint { get; init; } = Vector4.One;
    public Vector4 PatternColor { get; init; }
    public Vector4 PatternAccent { get; init; }
    public bool DefaultPattern { get; init; }
    public BackgroundDisplay Display { get; init; }
    public Vector2 Parallax { get; init; } = Vector2.One;
    public bool ImageSmoothing { get; init; } = true;
    public bool LockBackgroundRotation { get; init; }
    public bool LoopBackground { get; init; }
    public float BackgroundScale { get; init; } = 1f;
    public EventEnums.CamMovementType CameraRelativeTo { get; init; }
    public Vector2 CameraOffset { get; init; }
    public float CameraRotation { get; init; }
    public float? CameraZoom { get; init; }
    public bool LegacyCameraRelativeTo { get; init; }
    internal bool LegacyFilterTween { get; init; }

    public bool SkipAudio { get; init; }
    public bool PulseOnFloor { get; init; }
    public float MusicVolume { get; init; } = 1f;
    public float HitsoundVolume { get; init; } = 1f;
    public string Hitsound { get; init; } = "Kick";
    public EventEnums.Ease PlanetEase { get; init; } = EventEnums.Ease.Linear;
    public int PlanetEaseParts { get; init; } = 1;
    public bool MirrorPlanetEase { get; init; } = true;
    public float TileLength { get; init; } = 1f;
    public string? BackgroundVideo { get; init; }

    public static LevelPlaybackSettings Read(Level level) => Read(level.root["settings"] as JObject);

    internal static LevelPlaybackSettings Read(JObject? settings)
    {
        string? Text(string key) => Setting<string?>(settings, key, null);
        bool Flag(string key, bool fallback = false) => Setting(settings, key, fallback);
        float Number(string key, float fallback) => Finite(Setting(settings, key, fallback), fallback);
        Vector2 Pair(string key, Vector2 fallback) =>
            Setting<float?[]?>(settings, key, null) is { Length: >= 2 } pair
                ? new Vector2(Finite(pair[0] ?? fallback.X, fallback.X), Finite(pair[1] ?? fallback.Y, fallback.Y))
                : fallback;
        Vector4 Color(string key, Vector4 fallback) => FloorTrackStateResolver.ParseColor(Text(key)) ?? fallback;
        float zoom = Number("zoom", float.NaN);
        return new LevelPlaybackSettings
        {
            Background = Color("backgroundColor", DefaultBackground),
            BackgroundImage = Text("bgImage"),
            ImageTint = Color("bgImageColor", Vector4.One),
            DefaultPattern = Flag("showDefaultBGIfNoImage") && Flag("showDefaultBGTile", true),
            PatternColor = Color("defaultBGTileColor", new Vector4(.06f, .07f, .13f, 1f)),
            PatternAccent = Text("defaultBGShapeType") == "Disabled" ? Vector4.Zero
                : Color("defaultBGShapeColor", Vector4.One),
            Display = Parse(Text("bgDisplayMode"), BackgroundDisplay.FitToScreen),
            Parallax = Pair("parallax", new Vector2(100f)) / 100f,
            ImageSmoothing = Flag("imageSmoothing", true),
            LockBackgroundRotation = Flag("lockRot"),
            LoopBackground = Flag("loopBG"),
            BackgroundScale = Math.Max(.001f, Number("scalingRatio", 100f) / 100f),
            CameraRelativeTo = Parse(Text("relativeTo"), EventEnums.CamMovementType.Player),
            CameraOffset = Pair("position", Vector2.Zero),
            CameraRotation = Number("rotation", 0f),
            CameraZoom = zoom >= 0f ? zoom : null,
            LegacyCameraRelativeTo = Setting(settings, "version", 13) < 11 || Flag("legacyCamRelativeTo"),
            LegacyFilterTween = Setting(settings, "version", 13) < 14 || Flag("legacyTween"),
            PulseOnFloor = Flag("pulseOnFloor"),
            MusicVolume = Math.Clamp(Number("volume", 100f), 0f, 100f) / 100f,
            HitsoundVolume = Math.Clamp(Number("hitsoundVolume", 100f), 0f, 100f) / 100f,
            Hitsound = Text("hitsound") ?? "Kick",
            PlanetEase = Parse(Text("planetEase"), EventEnums.Ease.Linear),
            PlanetEaseParts = Math.Max(1, Setting(settings, "planetEaseParts", 1)),
            MirrorPlanetEase = Text("planetEasePartBehavior") != "Repeat",
            TileLength = Text("tileShape") == "Short" ? 2f / 3f : 1f,
            BackgroundVideo = Text("bgVideo"),
        };
    }

    public static T Setting<T>(Level level, string key, T fallback) => Setting(level.root["settings"] as JObject, key, fallback);

    internal static T Setting<T>(JObject? settings, string key, T fallback)
    {
        try
        {
            return settings?[key] is { } token && token.ToObject<T>() is { } value ? value : fallback;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException
            or OverflowException or Newtonsoft.Json.JsonException)
        {
            return fallback;
        }
    }

    private static T Parse<T>(string? text, T fallback) where T : struct, Enum =>
        Enum.TryParse(text, true, out T value) && Enum.IsDefined(value) ? value : fallback;

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
}
