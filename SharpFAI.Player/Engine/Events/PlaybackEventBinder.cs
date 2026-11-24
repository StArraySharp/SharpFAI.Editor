using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine.Events;

internal static class PlaybackEventBinder
{
    private sealed class ParsedPreviewEvent : PreviewEvent;
    private sealed class ParsedRuntimeEvent : RuntimeEvent;

    /// <summary>Normalizes playback aliases before the NuGet parser sees them.</summary>
    public static Level ForPlayback(Level level)
    {
        JArray? actions = null;
        for (int i = level.actions.Count - 1; i >= 0; i--)
        {
            if (level.actions[i] is not JObject action)
            {
                continue;
            }

            string? type = (string?)action["eventType"];
            bool inactive = RemovesFromPlayback(action);
            if (!inactive && type != "PlanetRadius")
            {
                continue;
            }

            actions ??= (JArray)level.actions.DeepClone();
            if (inactive)
            {
                actions.RemoveAt(i);
            }
            else
            {
                actions[i]["eventType"] = "ScaleRadius";
            }
        }

        if (actions is null)
        {
            return level;
        }

        Dictionary<string, object> data = level.root.Properties().ToDictionary(item => item.Name, item => (object)item.Value);
        data["actions"] = actions;
        return new Level(data);
    }

    /// <summary>Bind editor fields once, keeping the parser's event as the source model.</summary>
    public static PlaybackEvent[] Bind(Level level, bool includeDecorations = false) =>
        Bind(level, includeDecorations, playbackOnly: false);

    public static PlaybackEvent[] BindForPlayback(Level level) => Bind(level, includeDecorations: true, playbackOnly: true);

    public static PlaybackEvent[] BindForEditor(Level playbackLevel, Level document) =>
        Bind(playbackLevel, includeDecorations: true, playbackOnly: true, document);

    private static PlaybackEvent[] Bind(Level level, bool includeDecorations, bool playbackOnly, Level? document = null)
    {
        IReadOnlyList<BaseEvent> parsed = level.DeserializeEvents();
        JsonSerializer serializer = JsonSerializer.Create(EventJsonConverter.GetJsonSettings());
        List<PlaybackEvent> result = new(playbackOnly ? Math.Min(parsed.Count, 4096) : parsed.Count);
        JObject? settings = level.root["settings"] as JObject;
        int index = 0;
        int sourceIndex = -1;
        foreach (JObject action in level.actions.OfType<JObject>())
        {
            if (document is not null)
                do { sourceIndex++; }
                while (document.actions[sourceIndex] is not JObject raw || RemovesFromPlayback(raw));
            BaseEvent source = parsed[index++];
            Type type = ResolveType(source.EventType);
            if (playbackOnly && (type == typeof(ParsedPreviewEvent) || type == typeof(ParsedRuntimeEvent)))
                continue;

            var item = Bind(action, source, type, serializer, settings);
            item.SourceIndex = sourceIndex;
            result.Add(item);
        }

        // The game reads a separate decorations array whenever present, regardless of the settings version.
        if (includeDecorations && (level.decorations ?? level.root["decorations"] as JArray) is { } decorations)
        {
            for (int decorationIndex = 0; decorationIndex < decorations.Count; decorationIndex++)
            {
                if (decorations[decorationIndex] is not JObject decoration)
                    continue;
                if (playbackOnly && (string?)decoration["eventType"] is not ("AddDecoration" or "AddText" or "AddObject" or "AddParticle"))
                    continue;
                using JsonReader reader = decoration.CreateReader();
                if (serializer.Deserialize<BaseEvent>(reader) is { } source)
                {
                    PlaybackEvent item = Bind(decoration, source, ResolveType(source.EventType), serializer, settings);
                    item.SourceIndex = decorationIndex;
                    if (item is AddImageDecoration image)
                        image.DecorationIndex = decorationIndex;
                    result.Add(item);
                }
            }
        }

        return result.ToArray();
    }

    private static bool RemovesFromPlayback(JObject action) =>
        (string?)action["eventType"] is "Hold" or "MultiPlanet" or "Pause" &&
        (string.Equals((string?)action["active"], "false", StringComparison.OrdinalIgnoreCase) ||
         string.Equals((string?)action["active"], "disabled", StringComparison.OrdinalIgnoreCase));

    internal static PlaybackEvent Rebind(JObject replacement, PlaybackEvent previous, JObject? settings)
    {
        var result = Bind(replacement, previous.Source!, previous.GetType(),
            JsonSerializer.Create(EventJsonConverter.GetJsonSettings()), settings);
        result.SourceIndex = previous.SourceIndex;
        if (previous is AddImageDecoration old && result is AddImageDecoration image)
            image.DecorationIndex = old.DecorationIndex;
        return result;
    }

    private static void MigrateDecoration(AddImageDecoration image, JObject decoration, JObject? settings)
    {
        if (image is not AddTextDecoration && !decoration.ContainsKey("decorationImage"))
            image.Image = (string?)decoration["decText"];
        if (image.Parallax is null && image.Depth is int depth && depth is not (1 or -1))
            image.Parallax = new JArray(depth, depth);

        if ((int?)settings?["version"] is > 12 ||
            decoration["components"] is not JValue { Value: string { Length: > 0 } components })
            return;

        JObject legacy;
        try
        {
            legacy = JObject.Parse("{" + components.Trim() + "}");
        }
        catch (JsonReaderException)
        {
            return;
        }

        if (legacy["scrLockToCamera"] is not JObject camera)
            return;
        var scale = image.Scale is { Count: >= 2 } values
            ? Math.Abs(((float?)values[0] ?? 100f) - ((float?)values[1] ?? 100f)) : 0f;
        if (camera["lockRot"]?.Type == JTokenType.Boolean && scale < .001f)
            image.LockRotation = (bool)camera["lockRot"]!;
        if (camera["lockScale"]?.Type == JTokenType.Boolean)
        {
            image.LockScale = (bool)camera["lockScale"]!;
            if (image.LockScale == true)
                image.ScaleMultiplier = ((float?)settings?["zoom"] ?? 100f) / 240f;
        }
        if (camera["lockPos"]?.Type == JTokenType.Boolean && (bool)camera["lockPos"]!)
            image.Parallax = new JArray(100, 100);
    }

    private static PlaybackEvent Bind(JObject sourceToken, BaseEvent source, Type type, JsonSerializer serializer,
        JObject? settings)
    {
        using JsonReader reader = sourceToken.CreateReader();
        PlaybackEvent item = (PlaybackEvent)serializer.Deserialize(reader, type)!;
        item.EventType = source.EventType;
        item.Source = source;
        if (item is AddObjectDecoration obj)
            obj.ObjectType = obj.ObjectType switch { "0" => "Floor", "1" => "Planet", "2" => "PlayerBubble", _ => obj.ObjectType };
        if (item is AddImageDecoration image)
            MigrateDecoration(image, sourceToken, settings);
        if (item is AddImageDecoration or MoveImageDecoration && (int?)settings?["version"] == 7 &&
            item.Active is null && sourceToken["enabled"]?.Type == JTokenType.Boolean)
            item.Active = (bool)sourceToken["enabled"]!;
        return item;
    }

    private static Type ResolveType(EventType type) => type switch
    {
        EventType.PositionTrack => typeof(PositionTrack),
        EventType.ScaleRadius => typeof(ScaleRadius),
        EventType.Hold => typeof(HoldTrack),
        EventType.ColorTrack => typeof(ColorTrack),
        EventType.ChangeTrack => typeof(ChangeTrack),
        EventType.AnimateTrack => typeof(AnimateTrack),
        EventType.MoveTrack => typeof(MoveTrack),
        EventType.RecolorTrack => typeof(RecolorTrack),
        EventType.TileDimensions => typeof(TileDimensions),
        EventType.Hide => typeof(Hide),
        EventType.SetFloorIcon => typeof(SetFloorIcon),
        EventType.RepeatEvents => typeof(RepeatEvents),
        EventType.MoveCamera => typeof(MoveCameraEvent),
        EventType.SetFilter or EventType.SetFilterAdvanced => typeof(CameraFilterEvent),
        EventType.ScreenTile or EventType.ScreenScroll or EventType.ScalePlanets or EventType.SetPlanetRotation
            or EventType.Bloom or EventType.ShakeScreen => typeof(SceneEffectEvent),
        EventType.AddDecoration => typeof(AddImageDecoration),
        EventType.AddText => typeof(AddTextDecoration),
        EventType.AddObject => typeof(AddObjectDecoration),
        EventType.AddParticle => typeof(AddParticleDecoration),
        EventType.SetParticle or EventType.EmitParticle => typeof(ParticleControlEvent),
        EventType.SetText => typeof(SetTextDecoration),
        EventType.SetObject => typeof(SetObjectDecoration),
        EventType.MoveDecorations => typeof(MoveImageDecoration),
        EventType.SetConditionalEvents => typeof(ConditionalDecorationEvent),
        EventType.Flash or EventType.HallOfMirrors or EventType.CustomBackground => typeof(BackdropEvent),
        EventType.Pause => typeof(PauseAngleEvent),
        EventType.ScaleMargin => typeof(ScaleMarginEvent),
        EventType.AutoPlayTiles => typeof(AutoPlayTilesEvent),
        EventType.Multitap => typeof(MultitapEvent),
        EventType.Checkpoint => typeof(CheckpointEvent),
        EventType.None or EventType.LevelSettings or EventType.SongSettings or EventType.TrackSettings
            or EventType.BackgroundSettings or EventType.CameraSettings or EventType.MiscSettings
            or EventType.EventSettings or EventType.DecorationSettings or EventType.SetSpeed or EventType.Twirl
            or EventType.Bookmark or EventType.EditorComment
            or EventType.MultiPlanet or EventType.FreeRoam or EventType.FreeRoamTwirl or EventType.FreeRoamRemove
            or EventType.FreeRoamWarning => typeof(ParsedPreviewEvent),
        _ => typeof(ParsedRuntimeEvent),
    };
}
