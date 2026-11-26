using Newtonsoft.Json.Linq;
using OpenTK.Mathematics;
using SharpFAI.Framework;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Filters;
using SharpFAI.Player.Engine.Play;
using SharpFAI.Serialization;
using Thrust.Audio;
using Thrust.Graphics;
using SharpFAI.Util;
using SharpFAI.ModApi;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    [Flags]
    internal enum EditorLayers
    {
        None = 0, Track = 1, Timing = 2, Rate = 4, Camera = 8, Filter = 16, Scene = 32,
        Decoration = 64, Backdrop = 128, Manual = 256, Audio = 512, Mixer = 1024,
        Bindings = 2048, CameraState = 4096, LegacyDecorations = 8192,
    }

    private const EditorLayers TimedLayers = EditorLayers.Camera | EditorLayers.Filter | EditorLayers.Scene |
        EditorLayers.Decoration | EditorLayers.Backdrop;

    internal readonly record struct EditorEventAppend(int Index, bool Decoration, JObject Value);
    internal sealed record EditorChartPatch(Action<JObject> Apply, bool TrackChanged, bool PreservesBindings,
        EditorEventAppend[]? AppendedEvents);

    private sealed record EditorTrackUpdate(List<Floor> Floors, Vector2[]? Remainders, IReadOnlyList<FloorTrackState> States,
        RuntimeTrackTimeline Runtime, HashSet<FloorMaterial>?[] Materials, VisibleFloorIndex? Visibility,
        FloorGeometry? Geometry, TrackGuideMesh? Guides);

    private sealed record EditorRevisionUpdate(Level Level, JObject SettingsJson, LevelPlaybackSettings Settings,
        EditorLayers Layers, PlaybackEvent[] Events, PlaybackTimeline Timeline)
    {
        public EditorChartPatch? PatchOnCommit;
        public JArray? TrackAngles;
        public bool ReusesEventSlots;
        public bool TriggersPrepared;
        public EditorTrackUpdate? Track;
        public RuntimeTrackTimeline? Runtime;
        public VisibleFloorIndex? Visibility;
        public TrackGuideMesh? Guides;
        public CameraTimeline? Camera;
        public FilterTimeline? Filter, BackgroundFilter;
        public SceneEffectTimeline? Scene;
        public ImageDecorationTimeline? Decoration;
        public BackdropTimeline? Backdrop;
        public ManualTrack? Manual;
        public CompiledModEffect[]? ModEffects;
        public (string? Path, DecodedAudio? Music, DecodedAudio? Hitsound)? Audio;
        public List<string> Warnings { get; } = [];

        public void Discard()
        {
            Track?.Geometry?.Dispose();
            Track?.Guides?.Dispose();
            Guides?.Dispose();
        }
    }

    private Task<EditorRevisionUpdate>? _editorRevisionTask;
    private EditorRevisionUpdate? _editorRevisionReady;
    private CancellationTokenSource? _editorRevisionCancellation;
    internal EditorLayers LastEditorRevisionLayers { get; private set; }
    internal string? EditorRevisionError { get; private set; }

    internal static EditorLayers EditorSettingLayers(string key) => key switch
    {
        "bpm" => EditorLayers.Timing | EditorLayers.Track,
        "offset" or "countdownTicks" or "separateCountdownTime" => EditorLayers.Timing,
        "pitch" => EditorLayers.Rate,
        "tileShape" => EditorLayers.Track | EditorLayers.Decoration,
        "trackStyle" or "trackColor" or "secondaryTrackColor" or "trackColorType" or
        "trackColorPulse" or "trackColorAnimDuration" or "trackPulseLength" or "trackGlowIntensity" or
        "trackShadowColor" or "trackTexture" or "trackTextureScale" or "trackAnimation" or
        "trackDisappearAnimation" or "beatsAhead" or "beatsBehind" or "floorIconOutlines" or "stickToFloors" => EditorLayers.Track,
        "relativeTo" or "position" or "rotation" or "legacyCamRelativeTo" => EditorLayers.Camera,
        "zoom" => EditorLayers.Camera | EditorLayers.LegacyDecorations,
        "legacyTween" => EditorLayers.Filter,
        "disableV15Features" => EditorLayers.Decoration,
        "version" => EditorLayers.Bindings | TimedLayers,
        "pulseOnFloor" or "planetEase" or "planetEaseParts" or "planetEasePartBehavior" => EditorLayers.CameraState,
        "backgroundColor" or "bgImage" or "bgImageColor" or "bgDisplayMode" or "parallax" or
        "imageSmoothing" or "lockRot" or "loopBG" or "scalingRatio" or "bgVideo" or "vidOffset" or "loopVideo" or "defaultBGTileColor" or
        "defaultBGShapeType" or "defaultBGShapeColor" or "showDefaultBGIfNoImage" or "showDefaultBGTile" => EditorLayers.Backdrop,
        "songFilename" or "hitsound" => EditorLayers.Audio,
        "volume" or "hitsoundVolume" => EditorLayers.Mixer,
        _ => EditorLayers.None,
    };

    private static EditorLayers EditorActionLayers(JToken? token)
    {
        if (TwirlEventToken.TryGetFloor(token, out _)) return EditorLayers.Timing | EditorLayers.Track;
        if (token is not JObject action) return EditorLayers.None;
        return (string?)action["eventType"] switch
        {
            "SetSpeed" or "Twirl" or "Hold" or "Pause" or "MultiPlanet" or "ScaleRadius" or "PlanetRadius" or
            "FreeRoam" or "FreeRoamTwirl" or "FreeRoamRemove" => EditorLayers.Timing | EditorLayers.Track,
            "PositionTrack" => EditorLayers.Track | EditorLayers.Decoration,
            "ColorTrack" or "ChangeTrack" or "AnimateTrack" or "MoveTrack" or "RecolorTrack" or
            "TileDimensions" or "Hide" or "SetFloorIcon" => EditorLayers.Track,
            "RepeatEvents" or "SetConditionalEvents" => EditorLayers.Track | TimedLayers,
            "MoveCamera" => EditorLayers.Camera,
            "SetFilter" or "SetFilterAdvanced" => EditorLayers.Filter,
            "ScreenTile" or "ScreenScroll" or "ScalePlanets" or "SetPlanetRotation" or "Bloom" or "ShakeScreen" => EditorLayers.Scene,
            "Flash" or "HallOfMirrors" or "CustomBackground" => EditorLayers.Backdrop,
            "AddDecoration" or "AddText" or "AddObject" or "AddParticle" or "MoveDecorations" or "SetText" or "SetObject" or
            "SetParticle" or "EmitParticle" => EditorLayers.Decoration,
            "ScaleMargin" or "AutoPlayTiles" or "Multitap" or "Checkpoint" => EditorLayers.Manual,
            _ => EditorLayers.None,
        };
    }

    private static EditorLayers ChangedActions(JArray before, JArray after)
    {
        int length = Math.Min(before.Count, after.Count);
        int first = 0;
        while (first < length && SameAction(before[first], after[first])) first++;
        if (first == before.Count && first == after.Count) return EditorLayers.None;
        int oldEnd = before.Count - 1, newEnd = after.Count - 1;
        while (oldEnd >= first && newEnd >= first && SameAction(before[oldEnd], after[newEnd]))
        {
            oldEnd--;
            newEnd--;
        }
        EditorLayers result = EditorLayers.Bindings;
        for (int i = first; i <= oldEnd; i++) result |= EditorActionLayers(before[i]);
        for (int i = first; i <= newEnd; i++) result |= EditorActionLayers(after[i]);
        // Insertion/removal shifts the source slots even for events outside the changed range.
        if (first != before.Count && before.Count != after.Count) result |= TimedLayers;
        return result;

        static bool SameAction(JToken before, JToken after) =>
            TwirlEventToken.TryGetFloor(before, out int floor)
                ? TwirlEventToken.TryGetFloor(after, out int other) && floor == other
                : JToken.DeepEquals(before, after);
    }

    internal static EditorLayers PlanEditorRevision(Level before, Level? after, JObject settings,
        EditorEventAppend[]? appendedEvents = null)
    {
        var old = (JObject)before.root["settings"]!;
        EditorLayers layers = EditorLayers.None;
        foreach (var property in settings.Properties())
            if (!JToken.DeepEquals(property.Value, old[property.Name])) layers |= EditorSettingLayers(property.Name);
        foreach (var property in old.Properties())
            if (!settings.ContainsKey(property.Name)) layers |= EditorSettingLayers(property.Name);
        if (appendedEvents is not null)
        {
            foreach (var append in appendedEvents)
                layers |= EditorLayers.Bindings | (append.Decoration ? EditorLayers.Decoration : EditorActionLayers(append.Value));
        }
        else if (after is not null)
        {
            if (!JToken.DeepEquals(before.root["angleData"], after.root["angleData"]))
                layers |= EditorLayers.Timing | EditorLayers.Track | EditorLayers.Bindings;
            layers |= ChangedActions(before.actions, after.actions);
            if (!JToken.DeepEquals(before.root["decorations"], after.root["decorations"]))
                layers |= EditorLayers.Decoration | EditorLayers.Bindings;
        }
        if (layers.HasFlag(EditorLayers.LegacyDecorations) &&
            (LevelPlaybackSettings.Setting(before, "version", 0) <= 12 || (int?)settings["version"] is null or <= 12))
            layers |= EditorLayers.Bindings | EditorLayers.Decoration;
        layers &= ~EditorLayers.LegacyDecorations;
        if (layers.HasFlag(EditorLayers.Timing)) layers |= TimedLayers | EditorLayers.Manual | EditorLayers.Audio;
        if (layers.HasFlag(EditorLayers.Track)) layers |= EditorLayers.Camera;
        return layers;
    }

    internal bool BeginEditorRevision(Level? snapshot, JObject settingsJson, EditorChartPatch? patch = null)
    {
        if (IsLoading || _level is not { } level || _editorTimeline is not { } previousTimeline ||
            _editorEvents is not { } previousEvents || _editorRevisionTask is not null || _editorRevisionReady is not null)
            return false;

        EditorRevisionError = null;
        var floors = _floors;
        var trackStates = _trackStates;
        var runtime = _trackRuntime!;
        var visibility = _visibleFloorIndex;
        var oldDecorations = _imageDecorations;
        int texturePercent = _loadedDecorationTexturePercent;
        var oldBackdrop = _backdrop;
        string path = LevelPath!;
        string? directory = Path.GetDirectoryName(path);
        int textureCapacity = _floorTextureCapacity;
        var audio = new EditorAudioReuse(_audioMusicPath, _audioMusic, _audioHitsound, previousTimeline.Pitch);
        var previousSettings = _settings;
        bool trackChanged = patch?.TrackChanged == true;
        EditorLayers immediate = snapshot is null && patch is null
            ? PlanEditorRevision(level, null, settingsJson) : EditorLayers.Bindings;
        if (!HasModIncrements && (immediate & ~(EditorLayers.Mixer | EditorLayers.CameraState)) == 0)
            _editorRevisionReady = Build(snapshot, immediate, CancellationToken.None);
        else
        {
            _editorRevisionCancellation = new CancellationTokenSource();
            var token = _editorRevisionCancellation.Token;
            _editorRevisionTask = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                Level? next = snapshot;
                var settingsLayers = PlanEditorRevision(level, null, settingsJson);
                bool compatibleBindings = (settingsLayers & EditorLayers.Bindings) == 0;
                var appends = patch?.AppendedEvents;
                var appendLayers = appends is not null ? PlanEditorRevision(level, null, settingsJson, appends) : EditorLayers.None;
                // Independent effect layers consume the new bound events and the current
                // timing. Keep the full JSON tree untouched until the revision commits.
                if (appends is not null && compatibleBindings && !level.EventsDeserialized &&
                    (appendLayers & (EditorLayers.Track | EditorLayers.Timing | EditorLayers.Audio)) == 0)
                {
                    return Build(null, appendLayers, token, appendedEvents: appends, patchOnCommit: patch);
                }
                bool anglesOnly = trackChanged && compatibleBindings && patch!.PreservesBindings && !HasModIncrements;
                if (patch is not null)
                {
                    // These edits only need the path and settings on the worker. Keep the existing
                    // action, decoration, and extension arrays intact until the revision commits.
                    var root = anglesOnly ? new JObject
                    {
                        ["angleData"] = level.angleData.DeepClone(),
                        ["settings"] = settingsJson.DeepClone(),
                    } : (JObject)level.root.DeepClone();
                    token.ThrowIfCancellationRequested();
                    patch.Apply(root);
                    // Transfer the detached tokens directly to Level so its cached angles and event
                    // model are built after the edit, without cloning the chart for a second time.
                    var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var property in root.Properties())
                    {
                        fields.Add(property.Name, property.Value);
                        property.Value = JValue.CreateNull();
                    }
                    next = new Level(fields);
                }
                // Editing the path invalidates timing and all its dependent layers. Tile patches
                // already identify the change, so a token-by-token diff adds no information.
                if (!trackChanged)
                {
                    var planned = appends is not null ? appendLayers :
                        next is null ? immediate : PlanEditorRevision(level, next, settingsJson);
                    return Build(next, planned, token, appendedEvents: compatibleBindings ? appends : null);
                }
                var layers = settingsLayers | EditorLayers.Track | EditorLayers.Timing | EditorLayers.Bindings |
                    TimedLayers | EditorLayers.Manual | EditorLayers.Audio;
                return Build(next, layers, token, relocateBindings: compatibleBindings,
                    preserveBindings: compatibleBindings && patch!.PreservesBindings, anglesOnly: anglesOnly);
            }, token);
        }
        return true;

        EditorRevisionUpdate Build(Level? next, EditorLayers layers, CancellationToken token,
            bool relocateBindings = false, bool preserveBindings = false, EditorEventAppend[]? appendedEvents = null,
            EditorChartPatch? patchOnCommit = null, bool anglesOnly = false)
        {
            bool Has(EditorLayers layer) => (layers & layer) != 0;
            var model = next ?? level;
            // Only full chart consumers need a new Level. Small settings deltas use a settings-only view.
            if (next is null && patchOnCommit is null &&
                Has(EditorLayers.Track | EditorLayers.Timing | EditorLayers.Bindings | EditorLayers.Audio))
                model = level.CreateSnapshot(path);
            if (!anglesOnly && !ReferenceEquals(model, level)) WriteEditorSettings(model, settingsJson);
            var view = ReferenceEquals(model, level) ? LevelPlaybackSettings.Read(settingsJson) : LevelPlaybackSettings.Read(model);
            var coreEvents = Has(EditorLayers.Track | EditorLayers.Timing)
                ? FloorCoreEvents.Read(anglesOnly ? level.actions : model.actions, model.angles.Count + 1, token) : null;
            var events = preserveBindings ? previousEvents :
                relocateBindings ? PlaybackEventBinder.RelocateEditorEvents(level, model, previousEvents, token) :
                appendedEvents is not null ? PlaybackEventBinder.AppendEditorEvents(previousEvents, appendedEvents, settingsJson) :
                Has(EditorLayers.Bindings) ? PlaybackEventBinder.BindForEditor(model, token) : previousEvents;
            var settings = view with { SkipAudio = previousSettings.SkipAudio };
            var nextFloors = Has(EditorLayers.Track) ? CreatePlaybackFloors(model, settings, coreEvents!) : floors;
            var timeline = Has(EditorLayers.Timing) ? BuildTimeline(model, nextFloors, events, coreEvents) : previousTimeline;
            if (Has(EditorLayers.Rate)) timeline = timeline with { Pitch = ReadPlaybackPitch(settingsJson) };
            var update = new EditorRevisionUpdate(anglesOnly ? level : model, settingsJson, settings, layers, events, timeline)
            {
                ReusesEventSlots = relocateBindings || ReferenceEquals(events, previousEvents),
                PatchOnCommit = patchOnCommit,
            };
            try
            {
                token.ThrowIfCancellationRequested();
                if (!anglesOnly && HasModIncrements)
                {
                    JObject? effectiveChart = null;
                    if (patchOnCommit is not null && ModReadsActions)
                    {
                        effectiveChart = (JObject)model.root.DeepClone();
                        patchOnCommit.Apply(effectiveChart);
                    }
                    RunChartProcessors(ChartPreparationStage.ValidateSource, model, null, token, settingsJson, effectiveChart);
                    var prepared = RunChartProcessors(ChartPreparationStage.PrepareBoundData, model, timeline.EntryTimes,
                        token, settingsJson, effectiveChart);
                    update.ModEffects = CompileModEffects(model, timeline.EntryTimes, timeline.FloorBpms, prepared, token);
                }
                if (Has(EditorLayers.Track))
                    update.Track = BuildEditorTrack(model, settings, events, nextFloors, timeline, directory, textureCapacity, token);
                else if (Has(EditorLayers.Timing | EditorLayers.Rate))
                {
                    update.Runtime = new RuntimeTrackTimeline(events, floors, runtime.Baseline,
                        timeline.EntryTimes, timeline.Pitch, timeline.FloorBpms);
                    update.Runtime.PrepareSeekCheckpoints(token);
                    update.Visibility = visibility?.WithTiming(update.Runtime, token);
                    if (Has(EditorLayers.Timing))
                        update.Guides = TrackGuideMesh.Build(floors, trackStates, runtime.Baseline, timeline,
                            Floor.length * 2f * settings.TileLength, token);
                }
                if (Has(EditorLayers.Camera))
                    update.Camera = BuildCameraTimeline(events, nextFloors, timeline, settings,
                        Floor.length * 2f * settings.TileLength, update.Track?.Runtime.Baseline ?? runtime.Baseline);
                if (Has(EditorLayers.Filter))
                {
                    update.Filter = FilterTimeline.Build(events, nextFloors, timeline.EntryTimes, timeline.FloorBpms, settings.LegacyFilterTween);
                    update.BackgroundFilter = FilterTimeline.Build(events, nextFloors, timeline.EntryTimes, timeline.FloorBpms,
                        settings.LegacyFilterTween, background: true);
                }
                if (Has(EditorLayers.Scene))
                    update.Scene = SceneEffectTimeline.Build(events, nextFloors, timeline.EntryTimes, timeline.FloorBpms);
                if (Has(EditorLayers.Decoration))
                    update.Decoration = ImageDecorationTimeline.Build(events, nextFloors, timeline.EntryTimes, timeline.FloorBpms,
                        directory, update.Warnings, token, UsesLegacyMasks(settingsJson), Floor.length * 2f * settings.TileLength,
                        editable: true, cachedImages: oldDecorations?.Images, texturePercent: texturePercent);
                if (Has(EditorLayers.Backdrop))
                    update.Backdrop = BackdropTimeline.Build(settings, events, nextFloors, timeline.EntryTimes, timeline.FloorBpms,
                        directory, update.Warnings, oldBackdrop?.Images, oldBackdrop?.Video, token);
                if (Has(EditorLayers.Manual | EditorLayers.Rate)) update.Manual = new ManualTrack(nextFloors, timeline, events);
                if (Has(EditorLayers.Audio | EditorLayers.Rate))
                    update.Audio = BuildAudio(model, path, nextFloors, timeline, settings, reuse: audio,
                        reuseHitsound: !Has(EditorLayers.Timing) && settings.Hitsound == previousSettings.Hitsound);
                // A track edit rebuilt every dependent timeline. Bind the new schedules
                // together before publishing them to the render thread.
                if (trackChanged && update.Decoration is { } decorations)
                {
                    decorations.BindTimedEffects(nextFloors, timeline.FloorBpms, update.Filter, update.BackgroundFilter,
                        update.Camera, update.Backdrop, update.Scene, update.Track!.Runtime);
                    update.TriggersPrepared = true;
                }
                token.ThrowIfCancellationRequested();
                if (anglesOnly)
                {
                    update.TrackAngles = model.angleData;
                    model.root["angleData"] = JValue.CreateNull();
                }
                return update;
            }
            catch
            {
                update.Discard();
                throw;
            }
        }
    }

    private static EditorTrackUpdate BuildEditorTrack(Level model, LevelPlaybackSettings settings, PlaybackEvent[] events,
        List<Floor> floors, PlaybackTimeline timeline, string? directory, int textureCapacity, CancellationToken token)
    {
        var (remainders, states, runtimeStates) = PlacePlaybackFloors(model, settings, events, floors, timeline, token);
        var runtime = new RuntimeTrackTimeline(events, floors, runtimeStates, timeline.EntryTimes, timeline.Pitch, timeline.FloorBpms);
        var (materials, visibility, geometry, guides) = BuildTrackGeometry(floors, states, runtime, timeline, settings, directory, textureCapacity, token);
        return new(floors, remainders, states, runtime, materials, visibility, geometry, guides);
    }

    private static bool UsesLegacyMasks(JObject settings) =>
        ((int?)settings["version"] ?? 0) < 15 || (bool?)settings["disableV15Features"] == true;

    private static void WriteEditorSettings(Level model, JObject settings)
    {
        // Level caches the settings object, so keep its identity.
        var target = (JObject)model.root["settings"]!;
        target.RemoveAll();
        foreach (var property in settings.Properties()) target.Add(property.Name, property.Value.DeepClone());
    }

    internal EditorEffectStatus FinishEditorRevision(bool apply = true)
    {
        var update = _editorRevisionReady;
        if (_editorRevisionTask is { } task)
        {
            if (!task.IsCompleted) return EditorEffectStatus.Pending;
            _editorRevisionTask = null;
            _editorRevisionCancellation?.Dispose();
            _editorRevisionCancellation = null;
            if (!task.IsCompletedSuccessfully)
            {
                EditorRevisionError = task.Exception?.GetBaseException().Message;
                return EditorEffectStatus.Failed;
            }
            update = task.Result;
        }
        _editorRevisionReady = null;
        if (update is null) return EditorEffectStatus.Failed;
        if (!apply)
        {
            update.Discard();
            return EditorEffectStatus.Failed;
        }
        try
        {
            ApplyEditorRevision(update);
            return EditorEffectStatus.Applied;
        }
        catch (Exception error)
        {
            EditorRevisionError = error.Message;
            return EditorEffectStatus.Failed;
        }
    }

    internal void RequestEditorRevisionCancellation() => _editorRevisionCancellation?.Cancel();

    internal void CancelEditorRevision()
    {
        _editorRevisionCancellation?.Cancel();
        _editorRevisionCancellation?.Dispose();
        _editorRevisionCancellation = null;
        if (_editorRevisionTask is { } task)
            _ = task.ContinueWith(static completed =>
            {
                if (completed.IsCompletedSuccessfully) completed.Result.Discard();
                else _ = completed.Exception;
            }, TaskScheduler.Default);
        _editorRevisionTask = null;
        _editorRevisionReady?.Discard();
        _editorRevisionReady = null;
    }

    private void ApplyEditorRevision(EditorRevisionUpdate update)
    {
        if (update.ModEffects is { } effects) _modEffects = effects;
        InvalidateModHistory();
        var layers = LastEditorRevisionLayers = update.Layers;
        bool Has(EditorLayers layer) => (layers & layer) != 0;
        bool rebindTriggers = Has(EditorLayers.Decoration | EditorLayers.Filter | EditorLayers.Camera |
            EditorLayers.Scene | EditorLayers.Backdrop | EditorLayers.Track | EditorLayers.Timing | EditorLayers.Rate);
        if (rebindTriggers)
            ResetDecorationTriggers();
        _level = update.Level;
        if (update.TrackAngles is { } path) _level.ReplaceAngleData(path);
        if (!JToken.DeepEquals(_level.root["settings"], update.SettingsJson)) WriteEditorSettings(_level, update.SettingsJson);
        if (Has(EditorLayers.Backdrop)) SetBackdrop(update.Backdrop);
        if (Has(EditorLayers.Camera)) _cameraTimeline = update.Camera;
        if (Has(EditorLayers.Filter))
        {
            _filterTimeline?.Dispose();
            _backgroundFilterTimeline?.Dispose();
            _filterTimeline = update.Filter;
            _backgroundFilterTimeline = update.BackgroundFilter;
            _backgroundPasses = [];
            _filterBackend?.ResetCameraHistory();
        }
        if (Has(EditorLayers.Scene))
        {
            _sceneEffects?.Dispose();
            _sceneEffects = update.Scene;
        }
        if (Has(EditorLayers.Filter | EditorLayers.Scene)) BeginShaderPrewarm();
        if (Has(EditorLayers.Decoration)) _imageDecorations = update.Decoration;
        if (update.Track is { } track)
        {
            InvalidatePositionTrackGuides();
            ClearFloorChunks();
            _visibleFloorIndex = track.Visibility;
            if (track.Geometry is { } geometry) CommitLoadedFloorGeometry(geometry);
            else _officialRenderer?.ClearLevel();
            _trackGuideRenderer?.Dispose();
            _trackGuideRenderer = track.Guides is { } guides ? new TrackGuideRenderer(_uploader, guides) : null;
            _floors = track.Floors;
            FloorCount = _floors.Count;
            _floorPositionRemainders = track.Remainders;
            _trackStates = track.States;
            _trackRuntime = track.Runtime;
            _trackMaterialOptions = track.Materials;
            _floorRenderTransforms = null;
            _floorRenderColors = null;
            _floorVisualTime = double.NaN;
            _trackStyle = ReadTrackStyle(_level);
            if (track.Visibility is { } visibility)
            {
                _levelBounds = visibility.Bounds;
                _hasBounds = true;
            }
            var origin = _floors[0].position;
            _cameraOrigin = new Vector2(origin.X, origin.Y);
            _selectedFloor = Math.Min(_selectedFloor, FloorCount - 1);
            _status = $"{Path.GetFileName(LevelPath)} — {FloorCount:N0} floors";
        }
        else if (update.Runtime is { } runtime)
        {
            _trackRuntime = runtime;
            _visibleFloorIndex = update.Visibility;
            _residentTime = FloorTimeRange.Empty;
            _floorVisualTime = double.NaN;
            if (Has(EditorLayers.Timing))
            {
                _trackGuideRenderer?.Dispose();
                _trackGuideRenderer = update.Guides is { } guides ? new TrackGuideRenderer(_uploader, guides) : null;
            }
        }
        if (rebindTriggers && !update.TriggersPrepared)
            BindDecorationEffects(update.Timeline.FloorBpms);
        if (Has(EditorLayers.Bindings) || Has(EditorLayers.Track)) SetEditorEvents(update.Events);
        if (Has(EditorLayers.Bindings) && !update.ReusesEventSlots)
        {
            _editorEventSlots = BuildSlots(update.Events);
            _editorLayerSlots = BuildLayerSlots(update.Events);
        }
        if (update.Manual is { } manual)
        {
            _manualTrack = manual;
            if (ManualAttemptActive)
            {
                if (Has(EditorLayers.Timing))
                {
                    ClearManualAttempt();
                    PlaySession.Start(manual, FloorAtTime(update.Timeline.EntryTimes, _playTime), _playTime);
                }
                else
                    PlaySession.UpdateTrackRules(manual);
            }
        }
        double previousPitch = _pitch;
        _settings = update.Settings;
        _editorTimeline = update.Timeline;
        if (Has(EditorLayers.Timing | EditorLayers.Rate)) SetTimelineData(update.Timeline);
        if (Has(EditorLayers.Timing | EditorLayers.Rate | EditorLayers.Audio | EditorLayers.Mixer))
        {
            var audio = update.Audio ?? (_audioMusicPath, _audioMusic, _audioHitsound);
            bool align = previousPitch != _pitch || !ReferenceEquals(audio.Item2, _audioMusic) ||
                !ReferenceEquals(audio.Item3, _audioHitsound);
            ApplyEditorAudio(audio.Item1, audio.Item2, audio.Item3, previousPitch);
            if (align)
            {
                _audioNeedsResync = true;
                SyncAudio();
            }
        }
        if (Has(EditorLayers.Timing | EditorLayers.Track | TimedLayers | EditorLayers.CameraState | EditorLayers.Rate))
        {
            _lastFilterTime = double.NaN;
            _colorHistory?.Reset();
            if (Has(EditorLayers.Timing | EditorLayers.Track | EditorLayers.Camera)) _cameraSnap = true;
            ApplyTimeline(previewCameraEvents: !_cameraFree,
                automaticConditions: rebindTriggers && (_isPlaying || _playTime > 0));
        }
        _warnings.AddRange(update.Warnings.Where(warning => !_warnings.Contains(warning)));
        update.PatchOnCommit?.Apply(_level.root);
    }
}
