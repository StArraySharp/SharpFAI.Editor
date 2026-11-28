using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;
using SharpFAI.Serialization;
using Thrust.Audio;
using ModRuntime = SharpFAI.Player.ModSupport.IModRuntime;
using Vector4 = System.Numerics.Vector4;

namespace SharpFAI.Player.Engine;

public sealed partial class LevelRenderer
{
    internal ModRuntime? Mods { get; set; }
    internal UiHost ModTarget { get; set; } = UiHost.Player;
    private CompiledModEffect[] _modEffects = [];
    private Dictionary<string, Filters.IFilterTarget>? _modAuxiliary;
    private int _modShaderGeneration;
    private ModSceneDepth? _modSceneDepth;
    private int _modDepthTexture;
    private bool _modHistoryInvalid = true;
    private (int Width, int Height, int Projection, UiHost Host) _modHistoryView;
    private double _modHistoryTime = double.NaN;
    private readonly record struct ChartCacheKey(int Generation, UiHost Host, ChartPreparationStage Stage, string Id, string Input,
        string Timing, string Upstream, string Settings);
    private readonly record struct AssetCacheKey(int Generation, UiHost Host, AssetKind Kind, string Name, string Id, string Input);
    private ModPreparationCache<ChartCacheKey, ChartPreparationResult>? _modChartCache;
    private readonly ModPreparationCache<AssetCacheKey, string> _modAssetCache = new(256);
    internal bool CanUpdateModEditorRevision => Mods is not { } mods ||
        (!mods.Entries<ChartProcessor>(ModTarget).Any(entry => entry.IsRegistered && entry.Value.Stage == ChartPreparationStage.PrepareChart) &&
         mods.Entries<AssetProcessor>(ModTarget).Count == 0 && mods.Entries<AudioHook>(ModTarget).Count == 0);
    internal bool HasModIncrements => Mods is { } mods && (mods.Entries<ChartProcessor>(ModTarget).Count != 0 ||
        mods.Entries<CustomEventCompiler>(ModTarget).Count != 0);

    private bool ModReadsActions => Mods?.Entries<ChartProcessor>(ModTarget).Any(entry =>
        entry.Value.Reads is null || entry.Value.Reads.RootFields.Contains("actions", StringComparer.Ordinal) ||
        entry.Value.Reads.RootFields.Contains("decorations", StringComparer.Ordinal)) == true;
    private readonly record struct CompiledModEffect(ModRegistration<CustomEventCompiler> Entry, TimedChartEvent Event);

    internal void RenderModExportFrame(OpenTK.Mathematics.Vector4 background)
    {
        var previous = ModTarget;
        ModTarget = UiHost.Export;
        try { RenderAfterClear(background); }
        finally { ModTarget = previous; }
    }

    internal void PostprocessModExport(string temporary, string output, CancellationToken token)
    {
        if (Mods is not { } mods) return;
        foreach (var entry in mods.Entries<Func<ExportArtifact, CancellationToken, ValueTask>>(UiHost.Export))
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Lifetime);
            string candidate = temporary + "." + Guid.NewGuid().ToString("N") + Path.GetExtension(output);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                File.Copy(temporary, candidate);
                entry.Value(new(candidate, Path.GetFileName(output)), cancellation.Token).AsTask().GetAwaiter().GetResult();
                cancellation.Token.ThrowIfCancellationRequested();
                if (new FileInfo(candidate).Length == 0)
                    throw new InvalidDataException("The export postprocessor produced an empty file.");
                File.Move(candidate, temporary, overwrite: true);
            }
            catch (Exception error) { HandleHookFailure(entry, error, token); }
            finally { File.Delete(candidate); }
        }
    }

    private bool HasModBackgroundPass() => Mods?.Entries<RenderProcessor>(ModTarget)
        .Any(entry => entry.IsRegistered && entry.Value.Stage != RenderStage.SceneAfterNative) == true;

    private void BeginModFrame()
    {
        _modDepthTexture = 0;
        if (Mods is not { } mods) return;
        int generation = mods.Generation;
        if (_modShaderGeneration != generation)
        {
            _modShaderGeneration = generation;
            _filterBackend?.ClearModShaders();
            _modSceneDepth?.Dispose();
            _modSceneDepth = null;
        }
        if (mods.Entries<RenderProcessor>(ModTarget).Count == 0) return;
        int projection = _sceneRenderView is not { } scene ? 0 : scene.RelativeProjection.Column3.Xyz.LengthSquared == 0 ? 1 : 2;
        var view = ((int)_camera.ViewportWidth, (int)_camera.ViewportHeight, projection, ModTarget);
        if (_modHistoryInvalid || view != _modHistoryView || _playTime < _modHistoryTime)
            _filterBackend?.ResetModHistory();
        _modHistoryInvalid = false;
        _modHistoryView = view;
        _modHistoryTime = _playTime;
    }

    private void InvalidateModHistory() => _modHistoryInvalid = true;

    private int DrawModDepth(int width, int height)
    {
        if (_modDepthTexture != 0) return _modDepthTexture;
        var scene = _sceneRenderView;
        var projection = scene?.WorldProjection ?? _camera.ViewProjection;
        var floors = scene?.RelativeProjection ?? projection;
        var origin = scene?.Origin ?? _camera.Position;
        int calls = DrawCalls;
        long triangles = DrawnTriangles;
        try
        {
            return _modDepthTexture = (_modSceneDepth ??= new()).Render(width, height, () =>
            {
                if (!(_sceneRenderView.HasValue || !_cameraRenderDisabled || _cameraFree)) return;
                if (_activeFloorChunks is { } chunks)
                    foreach (var chunk in chunks) DrawFloors(chunk.Renderer);
                else
                    DrawFloors(_officialRenderer);
                DrawFloors(_editorInsertedTrack);
                DrawFloors(_editorAngleTrack);
                if (_circleRenderer is not null && _floors.Count > 0)
                    DrawPlanets(projection, showTrail: false);
                if (ShowDecorations && !_decorationShaderFailed && _imageDecorations is { } decorations)
                    _decorationRenderer?.DrawDepth(decorations, _camera, scene.HasValue);
            });
        }
        finally { DrawCalls = calls; DrawnTriangles = triangles; }

        void DrawFloors(OfficialFloorRenderer? renderer) =>
            renderer?.Draw(floors, origin, width, height, ForceFloorIconOutlines, 0, int.MaxValue, scene.HasValue);
    }

    private void ReleaseModFrame()
    {
        if (_modAuxiliary is null) return;
        foreach (var texture in _modAuxiliary.Values) _filterBackend!.ReleaseTarget(texture);
        _modAuxiliary.Clear();
    }

    internal Level PrepareModChart(Level level, string path, CancellationToken token)
    {
        RunChartProcessors(ChartPreparationStage.ValidateSource, level, null, token);
        if (Mods?.Entries<ChartProcessor>(ModTarget).Any(entry => entry.Value.Stage == ChartPreparationStage.PrepareChart) != true)
            return level;
        var copy = level.CreateSnapshot(path);
        RunChartProcessors(ChartPreparationStage.PrepareChart, copy, null, token);
        return copy;
    }

    private IReadOnlyDictionary<string, JsonElement>? RunChartProcessors(
        ChartPreparationStage stage, Level level, double[]? times, CancellationToken token,
        JObject? settingsOverride = null, JObject? chartOverride = null)
    {
        if (Mods is not { } mods) return null;
        JsonElement? snapshot = null;
        string? timingKey = null;
        var generation = mods.LoadGeneration;
        Dictionary<string, JsonElement>? preparedData = null;
        Dictionary<string, string>? writtenSettings = null;
        foreach (var entry in mods.Entries<ChartProcessor>(ModTarget))
        {
            if (entry.Value.Stage != stage) continue;
            using var lease = entry.Acquire();
            if (lease is null) continue;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Lifetime);
            try
            {
                token.ThrowIfCancellationRequested();
                var root = chartOverride ?? level.root;
                JsonElement input = entry.Value.Reads is { } reads ? ReadModFields(root, settingsOverride, reads)
                    : snapshot ??= ReadModFields(root, settingsOverride, null);
                var upstream = preparedData is null ? null : new ReadOnlyDictionary<string, JsonElement>(preparedData);
                ChartCacheKey? resultKey = null;
                ChartPreparationResult? result = null;
                if (entry.Value.Reads is { } scope)
                {
                    resultKey = new(generation, ModTarget, stage, entry.Descriptor.Id, Digest(input.GetRawText()),
                        timingKey ??= times is null ? "" : Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(times.AsSpan()))),
                        upstream is null ? "" : Digest(JsonSerializer.Serialize(upstream)), scope.CacheVersion?.Invoke() ?? "");
                    LazyInitializer.EnsureInitialized(ref _modChartCache, () => new(128)).TryGet(resultKey.Value, out result);
                }
                bool uncached = result is null;
                result ??= entry.Value.Run(new(ModTarget, input, times is null ? null : Array.AsReadOnly(times), upstream),
                    cancellation.Token).AsTask().GetAwaiter().GetResult();
                cancellation.Token.ThrowIfCancellationRequested();
                if (uncached)
                    foreach (var diagnostic in result.Diagnostics ?? [])
                        mods.Report(entry.ModId, diagnostic.Floor is int floor ? $"#{floor} {diagnostic.Message}" : diagnostic.Message);
                if (stage == ChartPreparationStage.PrepareBoundData && result.Data is { ValueKind: not JsonValueKind.Undefined } data)
                    (preparedData ??= new(StringComparer.Ordinal))[entry.Descriptor.Id] = data.Clone();
                if (stage != ChartPreparationStage.PrepareChart || result.Operations is not { Count: > 0 } operations)
                {
                    if (uncached && resultKey is { } cacheKey) _modChartCache!.Store(cacheKey, Copy(result));
                    continue;
                }
                var keys = operations.OfType<SetChartSetting>().Select(operation => operation.Key).Distinct().ToArray();
                writtenSettings ??= new(StringComparer.Ordinal);
                foreach (string key in keys)
                    if (writtenSettings.TryGetValue(key, out string? owner) && entry.Descriptor.After?.Contains(owner) != true)
                        throw new InvalidDataException($"'{key}' already changed by '{owner}'.");
                var prepared = StageOperations(operations, level);
                foreach (var apply in prepared) apply();
                foreach (string key in keys) writtenSettings[key] = entry.Descriptor.Id;
                snapshot = null;
                if (uncached && resultKey is { } writeKey) _modChartCache!.Store(writeKey, Copy(result));
            }
            catch (Exception error) { HandleHookFailure(entry, error, token); }
        }
        return preparedData is null ? null : new ReadOnlyDictionary<string, JsonElement>(preparedData);

        static ChartPreparationResult Copy(ChartPreparationResult result) => new(result.Diagnostics?.ToArray(),
            result.Operations?.Select(operation => operation switch
            {
                SetChartSetting setting => (ChartOperation)(setting with { Value = setting.Value.Clone() }),
                AddChartEvent addition => addition with { Fields = addition.Fields.Clone() },
                _ => throw new InvalidDataException("Only settings and event additions can be cached as chart transforms."),
            }).ToArray(), result.Data?.Clone());
    }

    private static JsonElement ReadModFields(JObject root, JObject? settingsOverride, ChartPreprocessorReads? reads)
    {
        if (reads is null && settingsOverride is null) return ReadJson(root);
        var result = new JObject();
        foreach (string name in reads?.RootFields ?? root.Properties().Select(property => property.Name))
            if ((name == "settings" && settingsOverride is not null ? settingsOverride : root[name]) is { } value)
                result[name] = name == "settings" && value is JObject settings && reads?.SettingKeys is { } keys
                    ? new JObject(keys.Distinct(StringComparer.Ordinal).Where(key => settings[key] is not null)
                        .Select(key => new JProperty(key, settings[key]!.DeepClone())))
                    : value.DeepClone();
        return ReadJson(result);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static List<Action> StageOperations(IReadOnlyList<ChartOperation> operations, Level level)
    {
        var apply = new List<Action>(operations.Count);
        var settings = (JObject)level.root["settings"]!;
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case SetChartSetting setting when !string.IsNullOrWhiteSpace(setting.Key) && setting.Value.ValueKind != JsonValueKind.Undefined:
                    if (setting.Key is "bpm" or "pitch" && (!setting.Value.TryGetDouble(out double number) ||
                        !double.IsFinite(number) || number <= 0))
                        throw new InvalidDataException($"Invalid '{setting.Key}' setting.");
                    JToken value = JToken.Parse(setting.Value.GetRawText());
                    apply.Add(() => settings[setting.Key] = value);
                    break;
                case AddChartEvent addition when addition.Floor >= 0 && addition.Floor <= level.angles.Count &&
                                                 !string.IsNullOrWhiteSpace(addition.Type) && addition.Fields.ValueKind == JsonValueKind.Object:
                    var item = JObject.Parse(addition.Fields.GetRawText());
                    item["floor"] = addition.Floor;
                    item["eventType"] = addition.Type;
                    apply.Add(() => level.actions.Add(item));
                    break;
                default:
                    throw new InvalidDataException("PrepareChart supports setting changes and event additions.");
            }
        }
        return apply;
    }

    private void CheckModRequirements(Level level)
    {
        if (level.root["sharpFaiMods"]?["requirements"] is not JArray requirements) return;
        foreach (var request in requirements.OfType<JObject>())
        {
            if (request.Value<string>("id") is not { } id ||
                Mods?.IsActive(id, request.Value<string>("version") ?? "*", ModTarget) == true)
                continue;
            if (request.Value<bool?>("requiredForPlayback") == true && ModTarget == UiHost.Export && Mods?.SafeMode != true)
                throw new InvalidOperationException($"The chart requires Mod '{id}' to export its effects.");
            Mods?.Report(id, "The chart requests this Mod. Its effects are unavailable.");
        }
    }

    private CompiledModEffect[] CompileModEffects(Level level, double[] times, double[] bpms,
        IReadOnlyDictionary<string, JsonElement>? preparedData, CancellationToken token)
    {
        if (Mods is not { } mods) return [];
        var compilers = mods.Entries<CustomEventCompiler>(ModTarget);
        if (compilers.Count == 0 || level.root["sharpFaiMods"]?["events"] is not JArray events) return [];
        var byType = compilers.ToDictionary(entry => entry.Descriptor.Id, StringComparer.Ordinal);
        var result = new List<CompiledModEffect>();
        foreach (var item in events.OfType<JObject>())
        {
            token.ThrowIfCancellationRequested();
            if (item.Value<string>("type") is not { } type || !byType.TryGetValue(type, out var entry) ||
                item["anchor"]?["floor"]?.Value<int>() is not int floor || (uint)floor >= (uint)times.Length)
                continue;
            using var lease = entry.Acquire();
            if (lease is null) continue;
            try
            {
                int version = item.Value<int?>("schemaVersion") ?? 1;
                JsonElement payload = ReadJson(item["payload"] ?? new JObject());
                if (entry.Value.Schema is { } schema && version != schema.Version)
                {
                    if (version > schema.Version || schema.Migrate is null)
                        throw new InvalidDataException($"Unsupported schema version {version} for '{type}'.");
                    payload = schema.Migrate(version, payload);
                    version = schema.Version;
                }
                double offset = item["anchor"]?["angleOffsetDegrees"]?.Value<double>() ?? 0;
                double time = times[floor] + (bpms[floor] > 0 ? offset / 180 * 60 / bpms[floor] : 0);
                if (entry.Value.Compile(new(type, floor, time, payload, version, preparedData)) is { } effect &&
                    double.IsFinite(effect.Start) && double.IsFinite(effect.End) && effect.Start <= effect.End)
                    result.Add(new(entry, effect with
                    {
                        Payload = effect.Payload.ValueKind == JsonValueKind.Undefined ? default : effect.Payload.Clone(),
                    }));
            }
            catch (Exception error) { HandleHookFailure(entry, error, token); }
        }
        return result.OrderBy(effect => effect.Event.Start).ThenBy(effect => effect.Entry.Descriptor.Id, StringComparer.Ordinal).ToArray();
    }

    private void NotifyModJudgment(Play.HitRecord hit)
    {
        if (Mods is not { } mods) return;
        var observers = mods.Entries<JudgmentObserver>(ModTarget);
        if (observers.Count == 0) return;
        var judgment = new PlaybackJudgment(hit.Floor, hit.Time, hit.OffsetDegrees, hit.Margin.ToString(), hit.Accepted, hit.Failed);
        foreach (var entry in observers)
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            try { entry.Value.Notify(judgment); }
            catch (Exception error) { entry.Report(error); }
        }
    }

    private string PrepareModAsset(string path, AssetKind kind, CancellationToken token)
    {
        if (Mods is not { } mods) return path;
        string source = path;
        foreach (var entry in mods.Entries<AssetProcessor>(ModTarget))
        {
            if (entry.Value.Kind != kind) continue;
            using var lease = entry.Acquire();
            if (lease is null) continue;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, entry.Lifetime);
            try
            {
                string input = source;
                using var inputFile = File.OpenRead(input);
                var key = new AssetCacheKey(mods.LoadGeneration, ModTarget, kind, Path.GetFileName(path),
                    entry.Descriptor.Id, Convert.ToHexString(SHA256.HashData(inputFile)));
                if (_modAssetCache.TryGet(key, out string? cached) && cached is not null && File.Exists(cached))
                {
                    source = cached;
                    continue;
                }
                var result = entry.Value.Run(new(kind, Path.GetFileName(path), () => File.OpenRead(input)),
                    cancellation.Token).AsTask().GetAwaiter().GetResult();
                cancellation.Token.ThrowIfCancellationRequested();
                if (result is null) { _modAssetCache.Store(key, source); continue; }
                string extension = result.Extension.TrimStart('.').ToLowerInvariant();
                if (extension.Length is < 1 or > 8 || extension.Any(character => !char.IsAsciiLetterOrDigit(character)) ||
                    result.Content.Length is <= 0 or > 128 * 1024 * 1024)
                    throw new InvalidDataException("Invalid prepared asset format or size.");
                string directory = Path.Combine(mods.Root, "cache", "assets");
                Directory.CreateDirectory(directory);
                string hash = Convert.ToHexString(SHA256.HashData(result.Content.Span));
                string destination = Path.Combine(directory, hash + "." + extension);
                if (!File.Exists(destination))
                {
                    string temporary = destination + "." + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.WriteAllBytes(temporary, result.Content.Span);
                        try { File.Move(temporary, destination); }
                        catch (IOException) when (File.Exists(destination)) { }
                    }
                    finally { File.Delete(temporary); }
                }
                source = destination;
                _modAssetCache.Store(key, destination);
            }
            catch (Exception error) { HandleHookFailure(entry, error, token); }
        }
        return source;
    }

    internal DecodedAudio? ProcessModAudio(DecodedAudio? source, AudioBus bus, CancellationToken token, UiHost? target = null)
    {
        if (source is null || Mods is not { } mods) return source;
        byte[]? output = null;
        foreach (var entry in mods.Entries<AudioHook>(target ?? ModTarget))
        {
            if (entry.Value.Bus != bus) continue;
            using var lease = entry.Acquire();
            if (lease is null) continue;
            try
            {
                token.ThrowIfCancellationRequested();
                var next = output is not null ? (byte[])output.Clone() : source.IsStreamed ? source.ReadPcm(token) : (byte[])source.Pcm.Clone();
                entry.Value.Process(new(source.SampleRate, source.Channels), MemoryMarshal.Cast<byte, short>(next.AsSpan()));
                output = next;
            }
            catch (Exception error) { HandleHookFailure(entry, error, token); }
        }
        return output is null ? source : new() { SampleRate = source.SampleRate, Channels = source.Channels, Pcm = output };
    }

    private void ApplyModStage(RenderStage stage)
    {
        if (Mods is not { } mods) return;
        var entries = mods.Entries<RenderProcessor>(ModTarget);
        if (entries.Count == 0 && (stage != RenderStage.SceneAfterNative || _modEffects.Length == 0)) return;
        var frame = new RenderFrame(_playTime, (int)_camera.ViewportWidth, (int)_camera.ViewportHeight, _sceneRenderView is not null, ModTarget);
        FrameEffect combined = FrameEffect.Identity;
        foreach (var entry in entries)
        {
            if (entry.Value.Stage != stage) continue;
            using var lease = entry.Acquire();
            if (lease is null) continue;
            try
            {
                if (entry.Value.Enabled?.Invoke(frame) == false)
                {
                    if (entry.Value.Shader?.UsesHistory == true) _filterBackend?.DropModHistory(entry.Descriptor.Id);
                    continue;
                }
                if (entry.Value.Shader is { } shader)
                {
                    FlushColor();
                    var inputs = new List<Filters.IFilterTarget>();
                    foreach (string input in shader.Inputs ?? [])
                    {
                        if (_modAuxiliary is null || !_modAuxiliary.TryGetValue(input, out var texture))
                            throw new InvalidOperationException($"Auxiliary input '{input}' is unavailable for this frame.");
                        inputs.Add(texture);
                    }
                    Vector4 parameters = entry.Value.Parameters?.Invoke(frame) ?? Vector4.Zero;
                    if (!Finite(parameters)) throw new InvalidDataException("A Mod shader returned nonfinite parameters.");
                    int depth = shader.UsesSceneDepth ? DrawModDepth(frame.Width, frame.Height) : 0;
                    var target = (_filterBackend ??= new Filters.OpenGlFilterBackend()).ApplyModShader(
                        entry.Descriptor.Id, shader.Source, parameters, frame.Time, inputs, shader.ProducesAuxiliary,
                        depth, shader.UsesHistory);
                    if (target is not null)
                        (_modAuxiliary ??= new(StringComparer.Ordinal)).Add(entry.Descriptor.Id, target);
                }
                else if (entry.Value.Sample is { } sample)
                    Compose(sample(frame));
            }
            catch (Exception error) { RenderFailure(entry, error); }
        }
        if (stage == RenderStage.SceneAfterNative)
            foreach (var effect in _modEffects)
            {
                if (effect.Event.Start > frame.Time) break;
                if (effect.Event.End < frame.Time) continue;
                using var lease = effect.Entry.Acquire();
                if (lease is null) continue;
                try { Compose(effect.Entry.Value.Evaluate(effect.Event, frame)); }
                catch (Exception error) { RenderFailure(effect.Entry, error); }
            }
        FlushColor();

        void FlushColor()
        {
            if (combined == FrameEffect.Identity) return;
            (_filterBackend ??= new Filters.OpenGlFilterBackend()).ApplyModColor(combined.Multiply, combined.Add);
            combined = FrameEffect.Identity;
        }

        void Compose(FrameEffect effect)
        {
            var next = new FrameEffect(combined.Multiply * effect.Multiply, combined.Add * effect.Multiply + effect.Add);
            if (!Finite(next.Multiply) || !Finite(next.Add))
                throw new InvalidDataException("A Mod render pass returned nonfinite color parameters.");
            combined = next;
        }

        static bool Finite(Vector4 color) => float.IsFinite(color.X) && float.IsFinite(color.Y) &&
            float.IsFinite(color.Z) && float.IsFinite(color.W);

        void RenderFailure(ModRegistration entry, Exception error)
        {
            if (ModTarget == UiHost.Export) HandleHookFailure(entry, error, CancellationToken.None);
            else entry.Report(error);
        }
    }

    internal static void HandleHookFailure(ModRegistration entry, Exception error, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (error is OperationCanceledException && entry.Lifetime.IsCancellationRequested) return;
        entry.Report(error);
        if (entry.Descriptor.Required)
            throw new InvalidOperationException($"Required Mod hook '{entry.Descriptor.Id}' failed.", error);
    }

    private static JsonElement ReadJson(JToken source) =>
        JsonSerializer.Deserialize<JsonElement>(source.ToString(Newtonsoft.Json.Formatting.None));
}
