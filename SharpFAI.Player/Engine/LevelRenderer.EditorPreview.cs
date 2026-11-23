using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.Player.Engine.Events;
using SharpFAI.Player.Engine.Filters;
using SharpFAI.Serialization;

namespace SharpFAI.Player.Engine;

public sealed unsafe partial class LevelRenderer
{
    internal readonly record struct EditorEffectPatch(int Index, bool Decoration, JObject Value);
    internal enum EditorEffectStatus { Pending, Applied, Failed }

    [Flags]
    private enum EffectLayer { None = 0, Move = 1, Decoration = 2, Camera = 4, Filter = 8, Scene = 16, Backdrop = 32, Initial = 64 }

    private sealed record EffectUpdate(EffectLayer Layers, PlaybackEvent[] Events, EditorEffectPatch[] Patches,
        Dictionary<int, MoveImageDecoration> Motions)
    {
        public CameraTimeline? Camera;
        public FilterTimeline? Filter;
        public FilterTimeline? BackgroundFilter;
        public SceneEffectTimeline? Scene;
        public BackdropTimeline? Backdrop;
        public ImageDecorationTimeline? Decorations;
        public List<(AddImageDecoration Event, bool Decoration)> Initials { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    private PlaybackEvent[]? _editorEvents;
    private bool _editorHasCameraMotion;
    private PlaybackTimeline? _editorTimeline;

    private void SetEditorEvents(PlaybackEvent[]? events)
    {
        bool wasLocked = _editorHasCameraMotion;
        _editorEvents = events;
        _editorHasCameraMotion = events?.Any(item => item.IsActive && (uint)item.Floor < (uint)_floors.Count &&
            item.EventType is EventType.MoveCamera or EventType.ShakeScreen) == true;
        if (!wasLocked && _editorHasCameraMotion && _isPlaying)
        {
            ResetView();
            _cameraSnap = true;
        }
    }
    private Dictionary<(int Index, bool Decoration), int>? _editorEventSlots;
    private Task<EffectUpdate>? _editorEffectTask;
    private EffectUpdate? _editorEffectReady;
    private CancellationTokenSource? _editorEffectCancellation;
    private long _editorEffectStarted;
    private Dictionary<EffectLayer, int[]>? _editorLayerSlots;
    internal string LastEditorEffectLayers { get; private set; } = "";
    internal double LastEditorEffectMilliseconds { get; private set; }
    internal double LastEditorEffectMainThreadMilliseconds { get; private set; }
    internal string LastEditorEffectTimings { get; private set; } = "";

    internal static bool SupportsEditorEffect(string type) => type is
        "AddDecoration" or "AddText" or "AddObject" or "AddParticle" or "MoveDecorations" or "SetText" or "SetObject" or
        "SetParticle" or "EmitParticle" or "SetConditionalEvents" or
        "MoveCamera" or "SetFilter" or "SetFilterAdvanced" or "Flash" or "HallOfMirrors" or "CustomBackground" or
        "Bloom" or "ShakeScreen" or "ScreenTile" or "ScreenScroll" or "ScalePlanets" or "SetPlanetRotation";

    internal bool BeginEditorEffectUpdate(EditorEffectPatch[] patches)
    {
        if (IsLoading || _level is not { } level || _editorEvents is not { } previous ||
            _editorTimeline is not { } timeline || patches.Length == 0 ||
            _editorEffectTask is not null || _editorEffectReady is not null)
            return false;

        _editorEffectStarted = Stopwatch.GetTimestamp();
        _editorEventSlots ??= BuildSlots(previous);
        var next = (PlaybackEvent[])previous.Clone();
        EffectLayer layers = EffectLayer.None;
        Dictionary<int, MoveImageDecoration> motions = [];
        List<(AddImageDecoration Event, bool Decoration)> initials = [];
        var settings = _settings;
        var rawSettings = level.root["settings"] as JObject;
        var oldDecorations = _imageDecorations;
        var oldBackdrop = _backdrop;
        var oldCamera = _cameraTimeline;
        var oldFilter = _filterTimeline;
        var oldBackgroundFilter = _backgroundFilterTimeline;
        var eventSlots = _editorEventSlots;
        bool needsNewBackdropAsset = false;
        try
        {
            foreach (var patch in patches)
            {
                if (!_editorEventSlots.TryGetValue((patch.Index, patch.Decoration), out int slot))
                    return false;
                var before = previous[slot];
                if (!SupportsEditorEffect((string?)patch.Value["eventType"] ?? ""))
                    return false;
                var after = PlaybackEventBinder.Rebind(patch.Value, before, rawSettings);
                next[slot] = after;
                switch (after)
                {
                    case MoveImageDecoration move when move is not SetTextDecoration &&
                        oldDecorations?.CanEditMotion((MoveImageDecoration)before, move) == true:
                        motions.Add(move.SourceIndex, move);
                        layers |= EffectLayer.Move;
                        break;
                    case AddImageDecoration added when added is not (AddObjectDecoration or AddParticleDecoration) &&
                        oldDecorations?.EditableDecoration(patch.Index, patch.Decoration) is not null &&
                        CanEditDecorationParameters((JObject)(patch.Decoration ? (JArray)level.root["decorations"]! : level.actions)[patch.Index]!, patch.Value):
                        initials.Add((added, patch.Decoration));
                        layers |= EffectLayer.Initial;
                        break;
                    case MoveImageDecoration or AddImageDecoration or ParticleControlEvent or ConditionalDecorationEvent:
                        layers |= EffectLayer.Decoration;
                        break;
                    case MoveCameraEvent:
                        layers |= EffectLayer.Camera;
                        break;
                    case CameraFilterEvent:
                        layers |= EffectLayer.Filter;
                        break;
                    case SceneEffectEvent:
                        layers |= EffectLayer.Scene;
                        break;
                    case BackdropEvent backdrop:
                        layers |= EffectLayer.Backdrop;
                        if (backdrop.Enabled("bgImage", backdrop.Image) is { Length: > 0 } image)
                            needsNewBackdropAsset |= oldBackdrop?.Images.ContainsKey(image) != true;
                        break;
                    default:
                        return false;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        LastEditorEffectLayers = layers.ToString();
        var update = new EffectUpdate(layers, next, patches, motions);
        update.Initials.AddRange(initials);
        var floors = _floors;
        var directory = Path.GetDirectoryName(LevelPath);
        bool legacyMasks = oldDecorations?.LegacyMasks ?? ((int?)rawSettings?["version"] ?? 0) < 15 ||
            (bool?)rawSettings?["disableV15Features"] == true;
        float tileSize = TileSpacing;
        double bindingMs = Stopwatch.GetElapsedTime(_editorEffectStarted).TotalMilliseconds;
        var layerSlots = _editorLayerSlots ??= BuildLayerSlots(previous);
        PlaybackEvent[] LayerEvents(EffectLayer layer) => layerSlots[layer].Select(index => next[index]).ToArray();
        if (layers.HasFlag(EffectLayer.Decoration) || needsNewBackdropAsset)
        {
            _editorEffectCancellation = new CancellationTokenSource();
            CancellationToken token = _editorEffectCancellation.Token;
            _editorEffectTask = Task.Run(() => Prepare(token), token);
        }
        else
            _editorEffectReady = Prepare(CancellationToken.None);
        LastEditorEffectMainThreadMilliseconds = Stopwatch.GetElapsedTime(_editorEffectStarted).TotalMilliseconds;
        LastEditorEffectTimings = $"bind={bindingMs:F3}, prepare={LastEditorEffectMainThreadMilliseconds:F3}";
        return true;

        EffectUpdate Prepare(CancellationToken token)
        {
            if (layers.HasFlag(EffectLayer.Camera))
            {
                var replacements = patches.Where(patch => !patch.Decoration)
                    .Select(patch => next[eventSlots[(patch.Index, false)]])
                    .OfType<MoveCameraEvent>().ToDictionary(item => item.SourceIndex);
                update.Camera = oldCamera?.Edit(replacements, floors, timeline, settings, tileSize) ??
                    BuildCameraTimeline(next, floors, timeline, settings, tileSize, _trackRuntime?.Baseline);
            }
            if (layers.HasFlag(EffectLayer.Filter))
            {
                var replacements = patches.Where(patch => !patch.Decoration)
                    .Select(patch => next[eventSlots[(patch.Index, false)]])
                    .OfType<CameraFilterEvent>().ToDictionary(item => item.SourceIndex);
                update.Filter = oldFilter?.Edit(replacements, floors, timeline.FloorBpms) ??
                    FilterTimeline.Build(LayerEvents(EffectLayer.Filter), floors, timeline.EntryTimes, timeline.FloorBpms, settings.LegacyFilterTween);
                update.BackgroundFilter = oldBackgroundFilter?.Edit(replacements, floors, timeline.FloorBpms) ??
                    FilterTimeline.Build(LayerEvents(EffectLayer.Filter), floors, timeline.EntryTimes, timeline.FloorBpms,
                        settings.LegacyFilterTween, background: true);
            }
            if (layers.HasFlag(EffectLayer.Scene))
                update.Scene = SceneEffectTimeline.Build(LayerEvents(EffectLayer.Scene), floors, timeline.EntryTimes, timeline.FloorBpms);
            if (layers.HasFlag(EffectLayer.Backdrop))
            {
                var replacements = patches.Where(patch => !patch.Decoration)
                    .Select(patch => next[eventSlots[(patch.Index, false)]])
                    .OfType<BackdropEvent>().ToDictionary(item => item.SourceIndex);
                update.Backdrop = oldBackdrop?.EditFlashes(replacements, floors, timeline.FloorBpms) ??
                    BackdropTimeline.Build(settings, LayerEvents(EffectLayer.Backdrop), floors, timeline.EntryTimes, timeline.FloorBpms,
                        directory, update.Warnings, oldBackdrop?.Images);
            }
            if (layers.HasFlag(EffectLayer.Decoration))
                update.Decorations = ImageDecorationTimeline.Build(next, floors, timeline.EntryTimes, timeline.FloorBpms,
                    directory, update.Warnings, token, legacyMasks, tileSize, editable: true, cachedImages: oldDecorations?.Images);
            return update;
        }
    }

    internal EditorEffectStatus FinishEditorEffectUpdate(bool apply = true)
    {
        long started = Stopwatch.GetTimestamp();
        var update = _editorEffectReady;
        _editorEffectReady = null;
        if (_editorEffectTask is { } task)
        {
            if (!task.IsCompleted)
                return EditorEffectStatus.Pending;
            _editorEffectTask = null;
            _editorEffectCancellation?.Dispose();
            _editorEffectCancellation = null;
            if (!task.IsCompletedSuccessfully)
            {
                _ = task.Exception;
                return EditorEffectStatus.Failed;
            }
            update = task.Result;
        }
        if (!apply || update is null || _level is not { } level)
            return EditorEffectStatus.Failed;

        var layers = update.Layers;
        if (layers.HasFlag(EffectLayer.Camera))
            _cameraTimeline = update.Camera;
        if (layers.HasFlag(EffectLayer.Scene))
        {
            _sceneEffects?.Dispose();
            _sceneEffects = update.Scene;
        }
        if (layers.HasFlag(EffectLayer.Filter))
        {
            _filterTimeline?.Dispose();
            _filterTimeline = update.Filter;
            _backgroundFilterTimeline?.Dispose();
            _backgroundFilterTimeline = update.BackgroundFilter;
            _backgroundPasses = [];
            _filterBackend?.ResetCameraHistory();
        }
        if ((layers & (EffectLayer.Filter | EffectLayer.Scene)) != 0)
            BeginShaderPrewarm();
        if (layers.HasFlag(EffectLayer.Backdrop))
        {
            _backdrop = update.Backdrop;
            if (_backdrop?.NeedsBackgroundRenderer == true)
                _background ??= new BackgroundRenderer();
        }
        if (layers.HasFlag(EffectLayer.Decoration))
        {
            ResetDecorationTriggers();
            _imageDecorations = update.Decorations;
        }
        else if ((layers & (EffectLayer.Move | EffectLayer.Initial)) != 0)
        {
            ResetDecorationTriggers();
            LastEditorEffectTimings += $", reset={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}";
            foreach (var (item, decoration) in update.Initials)
                _imageDecorations?.EditInitial(item, decoration);
            if (layers.HasFlag(EffectLayer.Move))
                _imageDecorations?.EditMotions(update.Motions);
        }
        if ((layers & (EffectLayer.Decoration | EffectLayer.Filter)) != 0)
        {
            ResetDecorationTriggers();
            if (_imageDecorations is { } triggers)
                triggers.BindCameraFilters(_floors, _editorTimeline?.FloorBpms, _filterTimeline, _backgroundFilterTimeline);
            else
            {
                _filterTimeline?.SetAutomaticSchedule(null);
                _backgroundFilterTimeline?.SetAutomaticSchedule(null);
            }
        }
        LastEditorEffectTimings += $", apply={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}";
        long patchStarted = Stopwatch.GetTimestamp();
        foreach (var patch in update.Patches)
        {
            var target = patch.Decoration ? (JArray)level.root["decorations"]! : level.actions;
            target[patch.Index] = patch.Value;
        }
        SetEditorEvents(update.Events);
        _warnings.AddRange(update.Warnings.Where(warning => !_warnings.Contains(warning)));
        _colorHistory?.Reset();
        _lastFilterTime = double.NaN;
        LastEditorEffectTimings += $", patch={Stopwatch.GetElapsedTime(patchStarted).TotalMilliseconds:F3}";
        long sampleStarted = Stopwatch.GetTimestamp();
        ApplyTimeline(previewCameraEvents: !_cameraFree);
        LastEditorEffectTimings += $", sample={Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds:F3}";
        LastEditorEffectMilliseconds = Stopwatch.GetElapsedTime(_editorEffectStarted).TotalMilliseconds;
        LastEditorEffectMainThreadMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return EditorEffectStatus.Applied;
    }

    internal void CancelEditorEffectUpdate() => ClearEditorEffectUpdate();

    private void ClearEditorEffectUpdate()
    {
        _editorEffectCancellation?.Cancel();
        _editorEffectCancellation?.Dispose();
        _editorEffectCancellation = null;
        if (_editorEffectTask is { } task)
            _ = task.ContinueWith(completed => _ = completed.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _editorEffectTask = null;
        _editorEffectReady = null;
        _editorEventSlots = null;
        _editorLayerSlots = null;
    }

    private static Dictionary<EffectLayer, int[]> BuildLayerSlots(PlaybackEvent[] events)
    {
        Dictionary<EffectLayer, List<int>> slots = new()
        {
            [EffectLayer.Camera] = [], [EffectLayer.Filter] = [], [EffectLayer.Scene] = [], [EffectLayer.Backdrop] = [],
        };
        for (int index = 0; index < events.Length; index++)
        {
            if (events[index] is RepeatEvents)
            {
                foreach (var list in slots.Values)
                    list.Add(index);
                continue;
            }
            EffectLayer layer = events[index] switch
            {
                MoveCameraEvent => EffectLayer.Camera,
                CameraFilterEvent => EffectLayer.Filter,
                SceneEffectEvent => EffectLayer.Scene,
                BackdropEvent => EffectLayer.Backdrop,
                _ => EffectLayer.None,
            };
            if (layer != EffectLayer.None)
                slots[layer].Add(index);
        }
        return slots.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    internal static bool CanEditDecorationParameters(JObject before, JObject after)
    {
        foreach (string name in before.Properties().Select(property => property.Name)
            .Concat(after.Properties().Select(property => property.Name)).Distinct(StringComparer.Ordinal))
        {
            if (JToken.DeepEquals(before[name], after[name]))
                continue;
            if (name is not ("position" or "pivotOffset" or "relativeTo" or "rotation" or "scale" or
                "originalSize" or "scaleMultiplier" or "tile" or "color" or "opacity" or "depth" or
                "syncFloorDepth" or "imageSmoothing" or "blendMode" or "maskingType" or "maskingTarget" or
                "useMaskingDepth" or "maskingFrontDepth" or "maskingBackDepth" or "parallax" or
                "parallaxOffset" or "stickToFloor" or "lockRotation" or "lockScale" or "visible"))
                return false;
        }
        return true;
    }

    internal static Dictionary<(int Index, bool Decoration), int> BuildSlots(PlaybackEvent[] events)
    {
        Dictionary<(int, bool), int> slots = [];
        for (int index = 0; index < events.Length; index++)
            if (events[index] is { SourceIndex: >= 0 } item)
                slots[(item.SourceIndex, item is AddImageDecoration { DecorationIndex: >= 0 })] = index;
        return slots;
    }
}
