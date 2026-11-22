using Newtonsoft.Json.Linq;
using SharpFAI.Serialization;

namespace SharpFAI.Editor;

internal sealed record EditorField(string Key, string Type, JToken? Default = null,
    JArray? DisableIf = null, bool Hidden = false, string? Unit = null,
    bool Optional = false, bool StartEnabled = true, double? Minimum = null, double? Maximum = null,
    bool AllowEmpty = false, string[]? Options = null);

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

    public (string Key, string[] Events)[] Categories { get; private set; } =
    [
        ("Gameplay", ["SetSpeed", "Twirl", "Pause", "Hold", "MultiPlanet", "ScaleRadius", "SetHitsound", "Bookmark"]),
        ("TrackFx", ["PositionTrack", "ColorTrack", "AnimateTrack", "MoveTrack", "RecolorTrack"]),
        ("DecorationFx", ["MoveDecorations", "SetText", "SetParticle", "EmitParticle"]),
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
        foreach (string name in DecorationTypes)
            _events[name] = new(name, true, true, []);

        AddDefaults("SetSpeed", """{"speedType":"Bpm","beatsPerMinute":100.0,"bpmMultiplier":1.0,"angleOffset":0.0}""");
        AddDefaults("Pause", """{"duration":1.0,"countdownTicks":0,"angleCorrectionDir":"Backward"}""");
        AddDefaults("Hold", """{"duration":0,"distanceMultiplier":100,"landingAnimation":false}""");
        AddDefaults("MultiPlanet", """{"planets":"TwoPlanets"}""");
        AddDefaults("ScaleRadius", """{"scale":100.0}""");
        AddDefaults("SetHitsound", """{"gameSound":"Hitsound","hitsound":"Kick","hitsoundVolume":100}""");
        AddDefaults("EditorComment", """{"comment":""}""");
        AddDefaults("PositionTrack", """{"positionOffset":[0,0],"relativeTo":[0,"ThisTile"],"rotation":0.0,"scale":100.0,"opacity":100.0,"editorOnly":false,"stickToFloors":true,"justThisTile":false}""");
        AddDefaults("ColorTrack", """{"trackColorType":"Single","trackColor":"debb7b","secondaryTrackColor":"ffffff","trackStyle":"Standard"}""");
        AddDefaults("AnimateTrack", """{"trackAnimation":"None","beatsAhead":3.0,"trackDisappearAnimation":"None","beatsBehind":4.0}""");
        AddDefaults("MoveTrack", """{"startTile":[0,"ThisTile"],"endTile":[0,"ThisTile"],"duration":1.0,"positionOffset":[0,0],"rotationOffset":0.0,"scale":[100,100],"opacity":100.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("RecolorTrack", """{"startTile":[0,"ThisTile"],"endTile":[0,"ThisTile"],"trackColor":"debb7b","secondaryTrackColor":"ffffff","trackColorType":"Single","trackStyle":"Standard","angleOffset":0.0,"eventTag":""}""");
        AddDefaults("MoveCamera", """{"duration":1.0,"relativeTo":"Player","position":[0,0],"rotation":0.0,"zoom":100.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("MoveDecorations", """{"duration":1.0,"tag":"","positionOffset":[0,0],"rotationOffset":0.0,"scale":[100,100],"opacity":100.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("CustomBackground", """{"color":"000000","bgImage":"","imageColor":"ffffff","parallax":[100,100],"bgDisplayMode":"FitToScreen","angleOffset":0.0,"eventTag":""}""");
        AddDefaults("Flash", """{"duration":1.0,"plane":"Foreground","startColor":"ffffff","startOpacity":100.0,"endColor":"ffffff","endOpacity":0.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("SetFilter", """{"filter":"Grayscale","enabled":true,"intensity":100.0,"duration":0.0,"ease":"Linear","disableOthers":false,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("SetFilterAdvanced", """{"filter":"CameraFilterPack_AAA_SuperComputer","enabled":true,"disableOthers":false,"angleOffset":0.0,"duration":0.0,"ease":"Linear","filterProperties":{},"eventTag":""}""");
        AddDefaults("Bloom", """{"enabled":true,"threshold":50.0,"intensity":100.0,"color":"ffffff","duration":0.0,"angleOffset":0.0,"ease":"Linear","eventTag":""}""");
        AddDefaults("HallOfMirrors", """{"enabled":true,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("RepeatEvents", """{"repeatType":"Beat","repetitions":1,"interval":1.0,"tag":""}""");
        AddDefaults("SetText", """{"decText":"","tag":"","angleOffset":0.0,"eventTag":""}""");
        AddDefaults("SetParticle", """{"tag":"","targetMode":"Start","duration":0.0,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("EmitParticle", """{"tag":"","count":10,"angleOffset":0.0,"eventTag":""}""");
        AddDefaults("AddDecoration", """{"decorationImage":"","tag":"","relativeTo":"Global","position":[0,0],"pivotOffset":[0,0],"rotation":0.0,"scale":[100,100],"color":"ffffff","opacity":100.0,"depth":-1,"parallax":[0,0],"maskingType":"None"}""");
        AddDefaults("AddText", """{"decText":"","tag":"","font":"Default","relativeTo":"Global","position":[0,0],"rotation":0.0,"scale":[100,100],"color":"ffffff","opacity":100.0,"depth":-1,"parallax":[0,0]}""");
        AddDefaults("AddObject", """{"objectType":"Floor","tag":"","relativeTo":"Global","position":[0,0],"rotation":0.0,"scale":[100,100],"depth":-1}""");
        AddDefaults("AddParticle", """
            {"tag":"","autoPlay":true,"relativeTo":"Global","position":[0,0],"rotation":0.0,"scale":[100,100],"depth":-1,
             "maxParticles":1000,"simulationSpace":"Local","simulationSpeed":100.0,"playDuration":5.0,"loop":true,"randomSeed":0,
             "decorationImage":"","randomTextureTiling":[1,1],"startRotation":[0,0],"color":"ffffff","particleLifetime":[10,10],
             "particleSize":[100,100],"shapeType":"Circle","shapeRadius":1.0,"emissionRate":[10,10],"velocity":[[0,0],[0,0]],
             "velocityLimitOverLifetime":[0,0],"rotationOverTime":[0,0],"colorOverLifetime":"ffffff","sizeOverLifetime":[100,100],
             "arc":360.0,"arcMode":"Random"}
            """);

        JObject defaults = (JObject)Level.CreateNewLevel().root["settings"]!;
        _settings["SongSettings"] = Settings("songFilename", "bpm", "volume", "offset", "pitch", "hitsound", "hitsoundVolume", "countdownTicks");
        _settings["LevelSettings"] = Settings("artist", "song", "author", "levelDesc", "difficulty", "levelTags", "previewImage", "previewSongStart");
        _settings["TrackSettings"] = Settings("tileShape", "trackColorType", "trackColor", "secondaryTrackColor", "trackStyle", "trackTexture",
            "trackTextureScale", "trackAnimation", "beatsAhead", "trackDisappearAnimation", "beatsBehind");
        _settings["BackgroundSettings"] = Settings("backgroundColor", "bgImage", "bgImageColor", "bgDisplayMode", "parallax", "loopBG",
            "bgVideo", "vidOffset", "loopVideo");
        _settings["CameraSettings"] = Settings("relativeTo", "position", "rotation", "zoom", "pulseOnFloor");
        _settings["MiscSettings"] = Settings("stickToFloors", "floorIconOutlines", "planetEase", "planetEaseParts", "separateCountdownTime");

        if (source is not null)
            LoadSchema(JObject.Parse(File.ReadAllText(source)));

        EditorField[] Settings(params string[] keys) =>
            keys.Select(key => Infer(key, defaults[key]!)).ToArray();
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
            "MoveDecorations" => ["positionOffset", "rotationOffset", "scale", "opacity"],
            "AddParticle" => ["velocityLimitOverLifetime", "colorOverLifetime", "sizeOverLifetime"],
            _ => []
        };
        _events[type] = _events[type] with
        {
            Fields = JObject.Parse(json).Properties().Select(field =>
            {
                EditorField metadata = Infer(field.Name, field.Value);
                if (type == "AddParticle" && field.Name is "color" or "colorOverLifetime")
                    metadata = metadata with { Type = "MinMaxGradient" };
                return metadata with { Optional = optional.Contains(field.Name), StartEnabled = false };
            }).ToArray()
        };
    }

    private void LoadSchema(JObject schema)
    {
        if (schema["levelEvents"] is not JArray events || schema["categories"] is not JArray categories ||
            schema["settings"] is not JArray settings)
            throw new InvalidDataException(L.T("MalformedSchema"));

        _events.Clear();
        foreach (JObject item in events.OfType<JObject>().Where(item => item.Value<bool?>("pro") != true))
        {
            string type = item.Value<string>("name")!;
            _events[type] = new(type, item.Value<bool?>("isDecoration") == true,
                item.Value<bool?>("allowFirstFloor") != false, ReadFields(item));
        }
        Categories = categories.OfType<JObject>().Select(category =>
        {
            string key = category.Value<string>("name")!;
            string[] names = category["events"]!.Values<string>()
                .OfType<string>().Where(name => Find(name) is { Decoration: false } && name != "ChangeTrack").ToArray();
            return (key, names);
        }).Where(category => category.names.Length > 0).ToArray();
        if (Categories.Length == 0)
            throw new InvalidDataException(L.T("EmptySchema"));

        foreach (JObject item in settings.OfType<JObject>())
        {
            string name = item.Value<string>("name")!;
            if (name is not ("EventSettings" or "DecorationSettings"))
                _settings[name] = ReadFields(item);
        }
        SettingsTabs = _settings.Keys.ToArray();
    }

    private static EditorField[] ReadFields(JObject item) => (item["properties"] as JArray ?? []).OfType<JObject>()
        .Where(field => field.Value<bool?>("pro") != true && field.Value<bool?>("encode") != false)
        .Select(field =>
        {
            string type = field.Value<string>("type")!;
            JToken? value = field["default"];
            if (value is null && type == "MinMaxGradient")
                value = new JValue("ffffff");
            string[]? options = (field["options"] as JArray)?.OfType<JValue>().Select(option => option.ToString()).ToArray();
            if (options is null && type.StartsWith("Enum:", StringComparison.Ordinal))
                options = Options(new("", type));
            if (field["except"] is JArray exceptions && options is not null)
                options = options.Except(exceptions.Values<string>().OfType<string>(), StringComparer.Ordinal).ToArray();
            return new EditorField(field.Value<string>("name")!, type, value, field["disableIf"] as JArray,
                field.Value<string>("control") == "Hidden", field.Value<string>("unit"),
                field.Value<bool?>("canBeDisabled") == true, field.Value<bool?>("startEnabled") == true,
                field.Value<double?>("min"), field.Value<double?>("max"), field.Value<bool?>("allowEmpty") == true,
                options);
        })
        .ToArray();

    public static string[]? Options(EditorField field)
    {
        if (field.Options is { Length: > 0 }) return field.Options;
        if (field.Key == "filter" && field.Type == "AdvancedFilter") return EditorFilterCatalog.Names;
        string? name = field.Type.StartsWith("Enum:", StringComparison.Ordinal) ? field.Type[5..].Split('.').Last() : field.Key switch
        {
            "ease" => "Ease", "speedType" => "SpeedType", "hitsound" => "HitSound", "planets" => "PlanetCount",
            "angleCorrectionDir" => "AngleCorrectionDirection", _ => null
        };
        Type? type = typeof(EventEnums).GetNestedTypes().FirstOrDefault(type => type.IsEnum && type.Name == name);
        return type is null ? name is null ? null : EditorControlMetadata.EnumOptions(name)
            : Enum.GetNames(type).Where(value => value != "Unset" && !value.StartsWith("INTERNAL_", StringComparison.Ordinal)).ToArray();
    }

    public static EditorField Infer(string key, JToken value) => new(key, key == "filterProperties" ? "FilterProperties" :
        key == "filter" && value.ToString().StartsWith("CameraFilterPack", StringComparison.Ordinal) ? "AdvancedFilter" :
        (key.EndsWith("Color", StringComparison.OrdinalIgnoreCase) || key is "color" or "backgroundColor") && value.Type == JTokenType.String ? "Color" :
        value.Type switch
    {
        JTokenType.Integer => "Int",
        JTokenType.Float => "Float",
        JTokenType.Boolean => "Bool",
        JTokenType.Array when value.Count() == 2 && value.All(item => item.Type is JTokenType.Integer or JTokenType.Float or JTokenType.Null) => "Vector2",
        JTokenType.Array or JTokenType.Object or JTokenType.Null => "Json",
        _ => "String"
    }, value, AllowEmpty: value is JArray && value.Any(part => part.Type == JTokenType.Null));

    public static string Name(string key) => L.Name(key);
}
