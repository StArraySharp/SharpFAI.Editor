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

    private sealed record EditorTrackUpdate(List<Floor> Floors, Vector2[]? Remainders, FloorTrackState[] States,
        RuntimeTrackTimeline Runtime, HashSet<FloorMaterial>?[] Materials, VisibleFloorIndex? Visibility,
        FloorGeometry? Geometry, Mesh? Guides);

    private sealed record EditorRevisionUpdate(Level Level, JObject SettingsJson, LevelPlaybackSettings Settings,
        EditorLayers Layers, PlaybackEvent[] Events, PlaybackTimeline Timeline)
    {
        public EditorTrackUpdate? Track;
        public RuntimeTrackTimeline? Runtime;
        public VisibleFloorIndex? Visibility;
        public Mesh? Guides;
        public CameraTimeline? Camera;
        public FilterTimeline? Filter, BackgroundFilter;
        public SceneEffectTimeline? Scene;
        public ImageDecorationTimeline? Decoration;
        public BackdropTimeline? Backdrop;
        public ManualTrack? Manual;
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
        "imageSmoothing" or "lockRot" or "loopBG" or "scalingRatio" or "bgVideo" or "defaultBGTileColor" or
        "defaultBGShapeType" or "defaultBGShapeColor" or "showDefaultBGIfNoImage" or "showDefaultBGTile" => EditorLayers.Backdrop,
        "songFilename" or "hitsound" => EditorLayers.Audio,
        "volume" or "hitsoundVolume" => EditorLayers.Mixer,
        _ => EditorLayers.None,
    };

    private static EditorLayers EditorActionLayers(JToken? token)
    {
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
        while (first < length && JToken.DeepEquals(before[first], after[first])) first++;
        if (first == before.Count && first == after.Count) return EditorLayers.None;
        int oldEnd = before.Count - 1, newEnd = after.Count - 1;
        while (oldEnd >= first && newEnd >= first && JToken.DeepEquals(before[oldEnd], after[newEnd]))
        {
            oldEnd--;
            newEnd--;
        }
        EditorLayers result = EditorLayers.Bindings;
        for (int i = first; i <= oldEnd; i++) result |= EditorActionLayers(before[i]);
        for (int i = first; i <= newEnd; i++) result |= EditorActionLayers(after[i]);
        // Insertion/removal shifts the source slots even for events outside the changed range.
        if (before.Count != after.Count) result |= TimedLayers;
        return result;
    }

    internal static EditorLayers PlanEditorRevision(Level before, Level? after, JObject settings)
    {
        var old = (JObject)before.root["settings"]!;
        EditorLayers layers = EditorLayers.None;
        foreach (var property in settings.Properties())
            if (!JToken.DeepEquals(property.Value, old[property.Name])) layers |= EditorSettingLayers(property.Name);
        foreach (var property in old.Properties())
            if (!settings.ContainsKey(property.Name)) layers |= EditorSettingLayers(property.Name);
        if (after is not null)
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

    internal bool BeginEditorRevision(Level? snapshot, JObject settingsJson)
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
        EditorLayers immediate = snapshot is null ? PlanEditorRevision(level, null, settingsJson) : EditorLayers.Bindings;
        if ((immediate & ~(EditorLayers.Mixer | EditorLayers.CameraState)) == 0)
            _editorRevisionReady = Build(immediate, CancellationToken.None);
        else
        {
            _editorRevisionCancellation = new CancellationTokenSource();
            var token = _editorRevisionCancellation.Token;
            _editorRevisionTask = Task.Run(() => Build(snapshot is null ? immediate : PlanEditorRevision(level, snapshot, settingsJson), token), token);
        }
        return true;

        EditorRevisionUpdate Build(EditorLayers layers, CancellationToken token)
        {
            bool Has(EditorLayers layer) => (layers & layer) != 0;
            var model = snapshot ?? level;
            // Only full chart consumers need a new Level. Small settings deltas use a settings-only view.
            if (snapshot is null && Has(EditorLayers.Track | EditorLayers.Timing | EditorLayers.Bindings | EditorLayers.Audio))
                model = level.CreateSnapshot(path);
            if (!ReferenceEquals(model, level)) WriteEditorSettings(model, settingsJson);
            var view = ReferenceEquals(model, level) ? LevelPlaybackSettings.Read(settingsJson) : LevelPlaybackSettings.Read(model);
            var playbackModel = Has(EditorLayers.Track | EditorLayers.Timing | EditorLayers.Bindings)
                ? PlaybackEventBinder.ForPlayback(model) : model;
            var events = Has(EditorLayers.Bindings) ? PlaybackEventBinder.BindForEditor(playbackModel, model) : previousEvents;
            var settings = view with { SkipAudio = previousSettings.SkipAudio };
            var nextFloors = Has(EditorLayers.Track) ? CreatePlaybackFloors(playbackModel, settings) : floors;
            var timeline = Has(EditorLayers.Timing) ? BuildTimeline(playbackModel, nextFloors, events) : previousTimeline;
            if (Has(EditorLayers.Rate)) timeline = timeline with { Pitch = ReadPlaybackPitch(settingsJson) };
            var update = new EditorRevisionUpdate(model, settingsJson, settings, layers, events, timeline);
            try
            {
                token.ThrowIfCancellationRequested();
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
                {
                    update.Backdrop = BackdropTimeline.Build(settings, events, nextFloors, timeline.EntryTimes, timeline.FloorBpms,
                        directory, update.Warnings, oldBackdrop?.Images);
                    if (!string.IsNullOrWhiteSpace(settings.BackgroundVideo) && settings.BackgroundVideo != previousSettings.BackgroundVideo)
                        update.Warnings.Add("Background video playback is not supported.");
                }
                if (Has(EditorLayers.Manual | EditorLayers.Rate)) update.Manual = new ManualTrack(nextFloors, timeline, events);
                if (Has(EditorLayers.Audio | EditorLayers.Rate))
                    update.Audio = BuildAudio(model, path, nextFloors, timeline, settings, reuse: audio,
                        reuseHitsound: !Has(EditorLayers.Timing) && settings.Hitsound == previousSettings.Hitsound);
                token.ThrowIfCancellationRequested();
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
        var layers = LastEditorRevisionLayers = update.Layers;
        bool Has(EditorLayers layer) => (layers & layer) != 0;
        bool rebindTriggers = Has(EditorLayers.Decoration | EditorLayers.Filter | EditorLayers.Camera |
            EditorLayers.Scene | EditorLayers.Backdrop | EditorLayers.Track | EditorLayers.Timing | EditorLayers.Rate);
        if (rebindTriggers)
            ResetDecorationTriggers();
        _level = update.Level;
        if (!JToken.DeepEquals(_level.root["settings"], update.SettingsJson)) WriteEditorSettings(_level, update.SettingsJson);
        if (Has(EditorLayers.Backdrop))
        {
            _backdrop = update.Backdrop;
            if (_backdrop?.NeedsBackgroundRenderer == true) _background ??= new BackgroundRenderer();
        }
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
        if (rebindTriggers)
            BindDecorationEffects(update.Timeline.FloorBpms);
        if (Has(EditorLayers.Bindings) || Has(EditorLayers.Track)) SetEditorEvents(update.Events);
        if (Has(EditorLayers.Bindings))
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
    }
}
