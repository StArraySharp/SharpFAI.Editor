using Newtonsoft.Json.Linq;
using SharpFAI.Serialization;

namespace SharpFAI.Editor;

internal sealed record EditorField(string Key, string Type, JToken? Default = null,
    JArray? DisableIf = null, bool Hidden = false, string? Unit = null,
    bool Optional = false, bool StartEnabled = true, double? Minimum = null, double? Maximum = null,
    bool AllowEmpty = false, string[]? Options = null, JArray? ShowIf = null, JArray? HideIf = null,
    JArray? EnableIf = null, string? FileType = null, bool StringDropdown = false, bool Browsable = false);

internal sealed record EditorEvent(string Type, bool Decoration, bool AllowFirstFloor, EditorField[] Fields);

internal sealed class EditorCatalog
{
    private readonly Dictionary<string, EditorEvent> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EditorField[]> _settings = new(StringComparer.Ordinal);

    public static readonly (string Label, string[] Keys)[] ParticleTabs =
    [
        ("ParticleGeneral", ["maxParticles", "simulationSpace", "simulationSpeed", "playDuration", "loop", "randomSeed"]),
        ("ParticleShape", ["decorationImage", "randomTextureTiling", "startRotation", "color", "particleLifetime", "particleSize",
            "shapeType", "shapeRadius", "emissionRate"]),
        ("ParticleTransform", ["velocity", "velocityLimitOverLifetime", "rotationOverTime", "colorOverLifetime", "sizeOverLifetime",
            "arc", "arcMode"])
    ];

    public static readonly string[] DecorationTypes = ["AddDecoration", "AddText", "AddObject", "AddParticle"];

    private static readonly string[] PlanetEvents = ["MultiPlanet", "Hold", "ScaleRadius"];
    private static readonly string[] ExistingAudioEvents = ["SetHitsound", "PlaySound"];

    public (string Key, string[] Events)[] Categories { get; private set; } =
    [
        ("Gameplay", ["SetSpeed", "Twirl", "Pause", .. PlanetEvents, "Bookmark"]),
        ("TrackFx", ["PositionTrack", "ColorTrack", "AnimateTrack", "MoveTrack", "RecolorTrack"]),
        ("DecorationFx", ["MoveDecorations", "SetObject", "SetText", "SetParticle", "EmitParticle"]),
        ("VisualFx", ["CustomBackground", "MoveCamera", "Flash", "SetFilter", "SetFilterAdvanced", "Bloom", "HallOfMirrors"]),
        ("FxModifiers", ["RepeatEvents"]),
        ("Conveniences", ["EditorComment"])
    ];

    public string[] SettingsTabs { get; private set; } =
        ["SongSettings", "LevelSettings", "TrackSettings", "BackgroundSettings", "CameraSettings", "MiscSettings"];

    public EditorCatalog(string? source)
    {
        foreach (var category in Categories)
            foreach (string name in category.Events)
                _events.TryAdd(name, new(name, false, name != "SetSpeed", []));
        foreach (string name in ExistingAudioEvents)
            _events.Add(name, new(name, false, true, []));
        foreach (string name in DecorationTypes)
            _events[name] = new(name, true, true, []);

        AddDefaults("SetSpeed", """{"speedType":"Bpm","beatsPerMinute":100.0,"bpmMultiplier":1.0,"angleOffset":0.0}""");
        AddDefaults("Pause", """{"duration":1.0,"countdownTicks":0,"angleCorrectionDir":"Backward"}""");
        AddDefaults("Hold", """{"duration":0,"distanceMultiplier":100,"landingAnimation":false}""");
        AddDefaults("MultiPlanet", """{"planets":"TwoPlanets"}""");
        AddDefaults("ScaleRadius", """{"scale":100.0}""");
        AddDefaults("SetHitsound", """{"gameSound":"Hitsound","hitsound":"Kick","hitsoundVolume":100}""");
        AddDefaults("PlaySound", """{"hitsound":"Kick","offset":0,"playDuration":0,"pitch":100,"hitsoundVolume":100,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("EditorComment", """{"comment":""}""");
        AddDefaults("PositionTrack", """{"positionOffset":[0,0],"relativeTo":[0,"ThisTile"],"rotation":0.0,"scale":100.0,"opacity":100.0,"editorOnly":false,"stickToFloors":true,"justThisTile":false}""");
        AddDefaults("ColorTrack", """{"trackColorType":"Single","trackColor":"debb7b","secondaryTrackColor":"ffffff","trackStyle":"Standard"}""");
        AddDefaults("AnimateTrack", """{"trackAnimation":"None","beatsAhead":3.0,"trackDisappearAnimation":"None","beatsBehind":4.0}""");
        AddDefaults("MoveTrack", """{"startTile":[0,"ThisTile"],"endTile":[0,"ThisTile"],"duration":1.0,"positionOffset":[0,0],"rotationOffset":0.0,"scale":[100,100],"opacity":100.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("RecolorTrack", """{"startTile":[0,"ThisTile"],"endTile":[0,"ThisTile"],"trackColor":"debb7b","secondaryTrackColor":"ffffff","trackColorType":"Single","trackStyle":"Standard","angleOffset":0.0,"eventTag":""}""");
        AddDefaults("MoveCamera", """{"duration":1.0,"relativeTo":"Player","position":[0,0],"rotation":0.0,"zoom":100.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("MoveDecorations", """{"duration":1.0,"tag":"","positionOffset":[0,0],"cropPivotOffset":[0,0],"rotationOffset":0.0,"scale":[100,100],"opacity":100.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("CustomBackground", """{"color":"000000","bgImage":"","imageColor":"ffffff","parallax":[100,100],"bgDisplayMode":"FitToScreen","angleOffset":0.0,"eventTag":""}""");
        AddDefaults("Flash", """{"duration":1.0,"plane":"Foreground","startColor":"ffffff","startOpacity":100.0,"endColor":"ffffff","endOpacity":0.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("SetFilter", """{"filter":"Grayscale","enabled":true,"intensity":100.0,"duration":0.0,"ease":"Linear","disableOthers":false,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("SetFilterAdvanced", """{"filter":"CameraFilterPack_AAA_SuperComputer","enabled":true,"disableOthers":false,"angleOffset":0.0,"duration":0.0,"ease":"Linear","filterProperties":{},"eventTag":""}""");
        AddDefaults("Bloom", """{"enabled":true,"threshold":50.0,"intensity":100.0,"color":"ffffff","duration":0.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("HallOfMirrors", """{"enabled":true,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("RepeatEvents", """{"repeatType":"Beat","repetitions":1,"interval":1.0,"tag":""}""");
        AddDefaults("SetText", """{"decText":"","tag":"","angleOffset":0.0,"eventTag":""}""");
        AddDefaults("SetObject", """{"duration":1.0,"tag":"","angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("SetParticle", """{"tag":"","targetMode":"Start","duration":0.0,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("EmitParticle", """{"tag":"","count":10,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("AddDecoration", """{"decorationImage":"","tag":"","relativeTo":"Global","position":[0,0],"pivotOffset":[0,0],"cropPivotOffset":[0,0],"rotation":0.0,"scale":[100,100],"color":"ffffff","opacity":100.0,"depth":-1,"parallax":[0,0],"maskingType":"None"}""");
        AddDefaults("AddText", """{"decText":"","tag":"","font":"Default","relativeTo":"Global","position":[0,0],"rotation":0.0,"scale":[100,100],"color":"ffffff","opacity":100.0,"depth":-1,"parallax":[0,0]}""");
        AddDefaults("AddObject", """
            {"objectType":"Floor","tag":"","trackType":"Normal","trackAngle":180.0,"trackColorType":"Single",
             "trackColor":"debb7b","secondaryTrackColor":"ffffff","trackColorAnimDuration":2.0,"trackOpacity":100.0,
             "trackStyle":"Standard","trackIcon":"None","trackIconAngle":0.0,"trackIconFlipped":false,
             "trackRedSwirl":false,"trackGraySetSpeedIcon":false,"trackSetSpeedIconBpm":100.0,
             "trackGlowEnabled":false,"trackGlowColor":"ffffff","trackIconOutlines":false,
             "relativeTo":"Global","position":[0,0],"pivotOffset":[0,0],"rotation":0.0,"lockRotation":false,
             "scale":[100,100],"lockScale":false,"depth":-1,"syncFloorDepth":false,"parallax":[0,0],"parallaxOffset":[0,0]}
            """);
        EditorEvent setObject = _events["SetObject"];
        EditorField[] floorFields = _events["AddObject"].Fields
            .Where(field => IsObjectFloorField(field.Key) && field.Key is not ("trackType" or "trackSetSpeedIconBpm"))
            .Select(field => field with { ShowIf = null, DisableIf = null, Optional = true, StartEnabled = false })
            .ToArray();
        _events["SetObject"] = setObject with { Fields = [.. setObject.Fields[..2], .. floorFields, .. setObject.Fields[2..]] };
        AddDefaults("AddParticle", """
            {"tag":"","autoPlay":true,"relativeTo":"Global","position":[0,0],"rotation":0.0,"scale":[100,100],"depth":-1,
             "maxParticles":1000,"simulationSpace":"Local","simulationSpeed":100.0,"playDuration":5.0,"loop":true,"randomSeed":0,
             "decorationImage":"","randomTextureTiling":[1,1],"startRotation":[0,0],"color":"ffffff","particleLifetime":[10,10],
             "particleSize":[100,100],"shapeType":"Circle","shapeRadius":1.0,"emissionRate":[10,10],"velocity":[[0,0],[0,0]],
             "velocityLimitOverLifetime":[0,0],"rotationOverTime":[0,0],"colorOverLifetime":"ffffff","sizeOverLifetime":[100,100],
             "arc":360.0,"arcMode":"Random"}
            """);

        JObject defaults = (JObject)Level.CreateNewLevel().root["settings"]!;
        AddSettings("SongSettings", "songFilename", "bpm", "volume", "offset", "pitch", "hitsound", "hitsoundVolume", "countdownTicks");
        AddSettings("LevelSettings", "artist", "song", "author", "levelDesc", "difficulty", "levelTags", "previewImage", "previewSongStart");
        AddSettings("TrackSettings", "tileShape", "trackColorType", "trackColor", "secondaryTrackColor", "trackStyle", "trackTexture",
            "trackTextureScale", "trackAnimation", "beatsAhead", "trackDisappearAnimation", "beatsBehind");
        AddSettings("BackgroundSettings", "backgroundColor", "showDefaultBGIfNoImage", "showDefaultBGTile", "defaultBGTileColor",
            "defaultBGShapeType", "defaultBGShapeColor", "bgImage", "bgImageColor", "bgDisplayMode", "parallax", "loopBG",
            "bgVideo", "vidOffset", "loopVideo");
        AddSettings("CameraSettings", "relativeTo", "position", "rotation", "zoom", "pulseOnFloor");
        AddSettings("MiscSettings", "stickToFloors", "floorIconOutlines", "planetEase", "planetEaseParts", "separateCountdownTime");

        if (source is not null)
            LoadSchema(JObject.Parse(File.ReadAllText(source)));

        void AddSettings(string owner, params string[] keys) =>
            _settings[owner] = keys.Select(key => ApplyNumericBounds(owner, Infer(key, defaults[key]!, owner))).ToArray();
    }

    internal static EditorCatalog FromSchema(JObject schema)
    {
        var catalog = new EditorCatalog(null);
        catalog.LoadSchema(schema);
        return catalog;
    }

    public EditorEvent? Find(string type) => _events.GetValueOrDefault(type);
    public EditorField[] SettingsFor(string name) => _settings.GetValueOrDefault(name) ?? [];

    // The original editor's solo types are per floor; mutually exclusive types share a slot.
    public static string? ExclusiveEventSlot(string type) => type switch
    {
        "FreeRoam" => "Twirl",
        "Hold" => "Pause",
        "AnimateTrack" or "AutoPlayTiles" or "Bookmark" or "ChangeTrack" or "Checkpoint" or "ColorTrack" or
        "Hide" or "KillPlayer" or "Multitap" or "MultiPlanet" or "Pause" or "ScaleMargin" or "ScaleRadius" or
        "SetConditionalEvents" or "SetHitsound" or "SetHoldSound" or "SetPlanetRotation" or "TileDimensions" or
        "Twirl" => type,
        _ => null
    };

    public JObject Create(string type, int floor)
    {
        JObject result = new() { ["floor"] = floor, ["eventType"] = type };
        foreach (EditorField field in Find(type)?.Fields ?? [])
        {
            if (field.Default is { Type: not JTokenType.Null } && field.Key != "floor" &&
                (!field.Optional || field.StartEnabled))
                result[field.Key] = field.Default.DeepClone();
        }
        return result;
    }

    private void AddDefaults(string type, string json)
    {
        string[] optional = type switch
        {
            "PositionTrack" => ["positionOffset", "rotation", "scale", "opacity", "stickToFloors"],
            "MoveTrack" => ["positionOffset", "rotationOffset", "scale", "opacity"],
            "MoveCamera" => ["position", "rotation", "zoom"],
            "MoveDecorations" => ["positionOffset", "cropPivotOffset", "rotationOffset", "scale", "opacity"],
            "AddDecoration" => ["cropPivotOffset"],
            "AddParticle" => ["velocityLimitOverLifetime", "colorOverLifetime", "sizeOverLifetime"],
            _ => []
        };
        _events[type] = _events[type] with
        {
            Fields = JObject.Parse(json).Properties().Select(field =>
            {
                EditorField metadata = Infer(field.Name, field.Value, type);
                if (type == "AddParticle" && field.Name is "color" or "colorOverLifetime")
                    metadata = metadata with { Type = "MinMaxGradient" };
                if (type == "AddObject" && IsObjectFloorField(field.Name))
                    metadata = metadata with
                    {
                        ShowIf = ["objectType", "Floor"],
                        DisableIf = field.Name == "trackAngle" ? ["trackType", "Midspin"] : null,
                        Unit = field.Name switch
                        {
                            "trackAngle" or "trackIconAngle" => "°",
                            "trackOpacity" => "%",
                            "trackColorAnimDuration" => "seconds",
                            _ => metadata.Unit,
                        },
                    };
                if (type == "PlaySound")
                    metadata = field.Name switch
                    {
                        "offset" or "playDuration" => metadata with { Unit = "ms", Minimum = field.Name == "playDuration" ? 0 : null },
                        "pitch" => metadata with { Unit = "%", Minimum = 1, Maximum = 1000 },
                        "hitsoundVolume" => metadata with { Unit = "%", Minimum = 1, Maximum = 100 },
                        "angleOffset" => metadata with { Unit = "\u00b0" },
                        _ => metadata
                    };
                metadata = (type, field.Name) switch
                {
                    ("Hold", "duration") => metadata with { Unit = "loops", Minimum = 0 },
                    ("Hold", "distanceMultiplier") => metadata with { Unit = "%" },
                    ("ScaleRadius", "scale") => metadata with { Unit = "%", Minimum = -10000, Maximum = 10000 },
                    _ => metadata,
                };
                return ApplyNumericBounds(type, metadata with { Optional = optional.Contains(field.Name), StartEnabled = false });
            }).ToArray()
        };
    }

    private static bool IsObjectFloorField(string key) =>
        key.StartsWith("track", StringComparison.Ordinal) || key == "secondaryTrackColor";

    private void LoadSchema(JObject schema)
    {
        if (schema["levelEvents"] is not JArray events || schema["categories"] is not JArray categories ||
            schema["settings"] is not JArray settings)
            throw new InvalidDataException(L.T("MalformedSchema"));

        var builtInEvents = PlanetEvents.Concat(ExistingAudioEvents).Select(name => _events[name]).ToArray();
        _events.Clear();
        foreach (JObject item in events.OfType<JObject>().Where(item => item.Value<bool?>("pro") != true))
        {
            string type = item.Value<string>("name")!;
            _events[type] = new(type, item.Value<bool?>("isDecoration") == true,
                item.Value<bool?>("allowFirstFloor") != false, ReadFields(item));
        }
        foreach (var definition in builtInEvents)
            _events.TryAdd(definition.Type, definition);

        var loadedCategories = categories.OfType<JObject>().Select(category =>
        {
            string key = category.Value<string>("name")!;
            var names = category["events"]!.Values<string>().OfType<string>()
                .Where(name => Find(name) is { Decoration: false } && name != "ChangeTrack" &&
                    !ExistingAudioEvents.Contains(name));
            names = key == "Gameplay"
                ? names.Concat(PlanetEvents).Distinct(StringComparer.Ordinal)
                : names.Except(PlanetEvents, StringComparer.Ordinal);
            return (Key: key, Events: names.ToArray());
        }).ToList();
        if (loadedCategories.Count == 0)
            throw new InvalidDataException(L.T("EmptySchema"));
        if (!loadedCategories.Any(category => category.Key == "Gameplay"))
            loadedCategories.Insert(0, ("Gameplay", PlanetEvents));
        Categories = loadedCategories.Where(category => category.Events.Length > 0).ToArray();

        foreach (JObject item in settings.OfType<JObject>())
        {
            string name = item.Value<string>("name")!;
            if (name is not ("EventSettings" or "DecorationSettings"))
                _settings[name] = ReadFields(item);
        }
        SettingsTabs = _settings.Keys.ToArray();
    }

    private static EditorField[] ReadFields(JObject item)
    {
        string? owner = item.Value<string>("name");
        return (item["properties"] as JArray ?? []).OfType<JObject>()
            .Where(field => field.Value<bool?>("pro") != true && field.Value<bool?>("encode") != false)
            .Select(field =>
            {
                string key = field.Value<string>("name")!;
                string type = field.Value<string>("type")!;
                if (owner == "SetFilterAdvanced" && key == "filter" && type == "String")
                    type = "AdvancedFilter";
                JToken? value = field["default"];
                if (value is null && type == "MinMaxGradient")
                    value = new JValue("ffffff");
                var metadata = new EditorField(key, type, value, field["disableIf"] as JArray,
                    field.Value<string>("control") == "Hidden", field.Value<string>("unit"),
                    field.Value<bool?>("canBeDisabled") == true, field.Value<bool?>("startEnabled") == true,
                    ReadBound(field["min"]), ReadBound(field["max"]), field.Value<bool?>("allowEmpty") == true,
                    ShowIf: field["showIf"] as JArray, HideIf: field["hideIf"] as JArray,
                    EnableIf: field["enableIf"] as JArray, FileType: field.Value<string>("fileType"),
                    StringDropdown: field.Value<bool?>("stringDropdown") == true, Browsable: field.Value<bool?>("browsable") == true);
                string[]? options = field["options"] is JArray explicitOptions
                    ? explicitOptions.Values<string>().OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
                    : Options(metadata);
                if (field["except"] is JArray exceptions && options is not null)
                    options = options.Except(exceptions.Values<string>().OfType<string>(), StringComparer.Ordinal).ToArray();
                return ApplyNumericBounds(owner, metadata with { Options = options });
            })
            .ToArray();
    }

    private static EditorField ApplyNumericBounds(string? owner, EditorField field)
    {
        (double? Min, double? Max) bounds = (owner, field.Key) switch
        {
            ("SongSettings", "bpm") or ("SetSpeed", "beatsPerMinute") => (0.001, 10000),
            ("SetSpeed", "bpmMultiplier") => (1e-7, 128),
            ("SongSettings", "volume" or "hitsoundVolume") => (1, 100),
            ("SongSettings", "pitch") => (1, 1000),
            ("SongSettings", "offset") => (-10000, 300000),
            ("SongSettings", "countdownTicks") => (2, 12),
            _ => (null, null),
        };
        if (bounds.Min is null) return field;
        return field with
        {
            Type = (owner, field.Key) == ("SongSettings", "bpm") ? "Float" : field.Type,
            Minimum = field.Minimum is { } lower && bounds.Min is { } required
                ? Math.Max(lower, required) : field.Minimum ?? bounds.Min,
            Maximum = field.Maximum ?? bounds.Max,
        };
    }

    private static double? ReadBound(JToken? token)
    {
        if (token is JArray { Count: 2 } pair)
        {
            double? first = (pair[0] as JValue)?.Value<double?>();
            double? second = (pair[1] as JValue)?.Value<double?>();
            return first == second ? first : null;
        }
        return (token as JValue)?.Value<double?>();
    }

    private static readonly string[] EaseOptions = Enum.GetNames<EventEnums.Ease>()
        .Where(value => value != "Unset" && !value.StartsWith("INTERNAL_", StringComparison.Ordinal)).ToArray();

    public static string[] TileReferenceOptions { get; } = Enum.GetNames<EventEnums.TileRelativeTo>();

    public static string? EnumName(string type) => type.StartsWith("Enum:", StringComparison.Ordinal)
        ? type[(type.LastIndexOfAny([':', '.', '+']) + 1)..] : null;

    public static string[]? Options(EditorField field) => field.Options ?? (field.Type == "AdvancedFilter"
        ? EditorFilterCatalog.Names : EnumName(field.Type) switch
        {
            "Ease" => EaseOptions,
            "TileRelativeTo" => TileReferenceOptions,
            { } name => EditorControlMetadata.EnumOptions(name),
            _ => null
        });

    public static EditorField Infer(string key, JToken value, string? owner = null)
    {
        if (owner == "PlaySound" && key == "hitsound")
            return new(key, "String", value, FileType: "Audio", StringDropdown: true, Browsable: true);
        if (owner == "EditorComment" && key == "comment")
            return new(key, "Text", value);
        if (key == "filterProperties")
            return new(key, "FilterProperties", value);
        if (key == "filter" && (owner == "SetFilterAdvanced" || owner is null && value.Type == JTokenType.String &&
            value.ToString().StartsWith("CameraFilterPack", StringComparison.Ordinal)))
            return new(key, "AdvancedFilter", value);
        if (value is JArray { Count: 2 } tile && tile[1]?.Type == JTokenType.String && key is "relativeTo" or "startTile" or "endTile")
            return new(key, "Tile", value);
        if (EditorFieldEnums.For(key, owner) is { } enumName)
        {
            var field = new EditorField(key, "Enum:" + enumName, value);
            string[]? options = Options(field);
            options = (owner, key) switch
            {
                ("TrackSettings", "tileShape") => options?.Where(option => option != "Custom").ToArray(),
                ("CameraSettings", "relativeTo") => options?.Where(option => option is not ("LastPosition" or "LastPositionNoRotation")).ToArray(),
                ("AddParticle", "maskingType") => options?.Where(option => option != "Mask").ToArray(),
                _ => options
            };
            return field with { Options = options };
        }
        return new(key, (key.EndsWith("Color", StringComparison.OrdinalIgnoreCase) || key is "color" or "backgroundColor") &&
            value.Type == JTokenType.String ? "Color" : value.Type switch
            {
                JTokenType.Integer => "Int",
                JTokenType.Float => "Float",
                JTokenType.Boolean => "Bool",
                JTokenType.Array when value.Count() == 2 && value.All(item => item.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Null) => "Vector2",
                JTokenType.Array or JTokenType.Object or JTokenType.Null => "Json",
                _ => "String"
            }, value, AllowEmpty: value is JArray && value.Any(part => part.Type == JTokenType.Null));
    }

    public static string Name(string key) => L.Name(key);
}
