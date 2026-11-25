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

    internal static PlaybackEvent[] BindForEditor(Level document, CancellationToken token = default)
    {
        JsonSerializer serializer = JsonSerializer.Create(EventJsonConverter.GetJsonSettings());
        List<PlaybackEvent> result = [];
        var settings = document.root["settings"] as JObject;
        for (int index = 0; index < document.actions.Count; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            if (document.actions[index] is JObject action && BindEditorAction(action, index, serializer, settings) is { } item)
                result.Add(item);
        }
        BindDecorations(document, playbackOnly: true, serializer, settings, result);
        return result.ToArray();
    }

    internal static PlaybackEvent[] AppendEditorEvents(PlaybackEvent[] previous,
        LevelRenderer.EditorEventAppend[] appended, JObject settings)
    {
        JsonSerializer serializer = JsonSerializer.Create(EventJsonConverter.GetJsonSettings());
        List<PlaybackEvent> actions = [], decorations = [];
        foreach (var append in appended)
        {
            if (append.Decoration)
            {
                if (BindDecoration(append.Value, append.Index, playbackOnly: true, serializer, settings) is { } item)
                    decorations.Add(item);
            }
            else if (BindEditorAction(append.Value, append.Index, serializer, settings) is { } action)
                actions.Add(action);
        }
        if (actions.Count + decorations.Count == 0) return previous;

        int firstDecoration = actions.Count == 0 ? previous.Length :
            Array.FindIndex(previous, item => item is AddImageDecoration { DecorationIndex: >= 0 });
        if (firstDecoration < 0) firstDecoration = previous.Length;
        var events = new PlaybackEvent[previous.Length + actions.Count + decorations.Count];
        Array.Copy(previous, events, firstDecoration);
        actions.CopyTo(events, firstDecoration);
        Array.Copy(previous, firstDecoration, events, firstDecoration + actions.Count, previous.Length - firstDecoration);
        decorations.CopyTo(events, previous.Length + actions.Count);
        return events;
    }

    private static PlaybackEvent? BindEditorAction(JObject action, int index, JsonSerializer serializer, JObject? settings)
    {
        if (RemovesFromPlayback(action)) return null;
        // MoveDecorations is fully represented by its typed playback model.
        BaseEvent? source = (string?)action["eventType"] switch
        {
            "MoveDecorations" => new BaseEvent { EventType = EventType.MoveDecorations, Floor = action.Value<int?>("floor") ?? 0 },
            "PlanetRadius" => new BaseEvent { EventType = EventType.ScaleRadius, Floor = action.Value<int?>("floor") ?? 0 },
            _ => ReadSource(action, serializer),
        };
        if (source is null) return null;
        Type type = ResolveType(source.EventType);
        if (type == typeof(ParsedPreviewEvent) || type == typeof(ParsedRuntimeEvent)) return null;
        var item = Bind(action, source, type, serializer, settings);
        item.SourceIndex = index;
        return item;
    }

    internal static PlaybackEvent[] RelocateEditorEvents(Level before, Level after, PlaybackEvent[] previous,
        CancellationToken token)
    {
        JObject settings = (JObject)after.root["settings"]!;
        var oldDecorations = (JArray?)before.root["decorations"];
        var newDecorations = (JArray?)after.root["decorations"];
        var relocated = new PlaybackEvent[previous.Length];
        JsonSerializer serializer = JsonSerializer.Create(EventJsonConverter.GetJsonSettings());
        for (int i = 0; i < previous.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            var item = previous[i];
            bool decoration = item is AddImageDecoration { DecorationIndex: >= 0 };
            var old = (JObject)(decoration ? oldDecorations! : before.actions)[item.SourceIndex];
            var updated = (JObject)(decoration ? newDecorations! : after.actions)[item.SourceIndex];
            bool changedReference = item is TrackRangeEvent &&
                (!JToken.DeepEquals(old["startTile"], updated["startTile"]) ||
                 !JToken.DeepEquals(old["endTile"], updated["endTile"])) ||
                item is PositionTrack && !JToken.DeepEquals(old["relativeTo"], updated["relativeTo"]);
            relocated[i] = changedReference
                ? Rebind(updated, item, settings, serializer)
                : item.WithFloor(updated.Value<int?>("floor") ?? 0);
        }
        return relocated;
    }

    // Floor construction and timing use the core events. The remaining events are consumed
    // by the separately bound playback layers, which retain their original source indices.
    internal static Level ForEditorFloorTiming(Level level)
    {
        JArray actions = new();
        foreach (JObject action in level.actions.OfType<JObject>())
            if ((string?)action["eventType"] is { } name &&
                Enum.TryParse<EventType>(name, out var type) &&
                type is (EventType.SetSpeed or EventType.Twirl or EventType.Hold or EventType.MultiPlanet or
                         EventType.Pause or EventType.FreeRoam) && !RemovesFromPlayback(action))
                actions.Add(action);

        return new Level(new Dictionary<string, object>
        {
            ["angleData"] = level.angleData,
            ["settings"] = level.root["settings"]!,
            ["actions"] = actions,
        });
    }

    private static PlaybackEvent[] Bind(Level level, bool includeDecorations, bool playbackOnly)
    {
        IReadOnlyList<BaseEvent> parsed = level.DeserializeEvents();
        JsonSerializer serializer = JsonSerializer.Create(EventJsonConverter.GetJsonSettings());
        List<PlaybackEvent> result = new(playbackOnly ? Math.Min(parsed.Count, 4096) : parsed.Count);
        JObject? settings = level.root["settings"] as JObject;
        int index = 0;
        foreach (JObject action in level.actions.OfType<JObject>())
        {
            BaseEvent source = parsed[index++];
            Type type = ResolveType(source.EventType);
            if (playbackOnly && (type == typeof(ParsedPreviewEvent) || type == typeof(ParsedRuntimeEvent)))
                continue;

            result.Add(Bind(action, source, type, serializer, settings));
        }

        if (includeDecorations) BindDecorations(level, playbackOnly, serializer, settings, result);
        return result.ToArray();
    }

    private static void BindDecorations(Level level, bool playbackOnly, JsonSerializer serializer, JObject? settings,
        List<PlaybackEvent> result)
    {
        // The game reads a separate decorations array whenever present, regardless of the settings version.
        if ((level.decorations ?? level.root["decorations"] as JArray) is { } decorations)
        {
            for (int decorationIndex = 0; decorationIndex < decorations.Count; decorationIndex++)
            {
                if (decorations[decorationIndex] is JObject decoration &&
                    BindDecoration(decoration, decorationIndex, playbackOnly, serializer, settings) is { } item)
                    result.Add(item);
            }
        }

    }

    private static PlaybackEvent? BindDecoration(JObject decoration, int index, bool playbackOnly,
        JsonSerializer serializer, JObject? settings)
    {
        if (playbackOnly && (string?)decoration["eventType"] is not ("AddDecoration" or "AddText" or "AddObject" or "AddParticle"))
            return null;
        if (ReadSource(decoration, serializer) is not { } source) return null;
        PlaybackEvent item = Bind(decoration, source, ResolveType(source.EventType), serializer, settings);
        item.SourceIndex = index;
        if (item is AddImageDecoration image) image.DecorationIndex = index;
        return item;
    }

    private static BaseEvent? ReadSource(JObject action, JsonSerializer serializer)
    {
        using JsonReader reader = action.CreateReader();
        return serializer.Deserialize<BaseEvent>(reader);
    }

    private static bool RemovesFromPlayback(JObject action) =>
        (string?)action["eventType"] is "Hold" or "MultiPlanet" or "Pause" &&
        (string.Equals((string?)action["active"], "false", StringComparison.OrdinalIgnoreCase) ||
         string.Equals((string?)action["active"], "disabled", StringComparison.OrdinalIgnoreCase));

    internal static PlaybackEvent Rebind(JObject replacement, PlaybackEvent previous, JObject? settings) =>
        Rebind(replacement, previous, settings, JsonSerializer.Create(EventJsonConverter.GetJsonSettings()));

    private static PlaybackEvent Rebind(JObject replacement, PlaybackEvent previous, JObject? settings,
        JsonSerializer serializer)
    {
        var result = Bind(replacement, previous.Source!, previous.GetType(), serializer, settings);
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
