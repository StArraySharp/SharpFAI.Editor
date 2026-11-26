using System.Numerics;
using System.Text.Json;

namespace SharpFAI.ModApi;

public sealed record HookDescriptor(string Id, UiHost Hosts = UiHost.All, int Order = 0,
    IReadOnlyList<string>? After = null, bool Required = false);

public enum ChartPreparationStage { ValidateSource, PrepareChart, PrepareBoundData }
/// <summary>Declares the JSON fields supplied to a deterministic cached processor.</summary>
public sealed record ChartPreprocessorReads(IReadOnlyList<string> RootFields, IReadOnlyList<string>? SettingKeys = null)
{
    public Func<string>? CacheVersion { get; init; }
}
public sealed record ChartPreparationContext(UiHost Host, JsonElement Chart, IReadOnlyList<double>? EntryTimes,
    IReadOnlyDictionary<string, JsonElement>? PreparedData = null);
public sealed record ChartDiagnostic(string Message, int? Floor = null);
public sealed record ChartPreparationResult(IReadOnlyList<ChartDiagnostic>? Diagnostics = null,
    IReadOnlyList<ChartOperation>? Operations = null, JsonElement? Data = null);
public interface IChartPreprocessorRegistry
{
    IDisposable Register(HookDescriptor descriptor, ChartPreparationStage stage,
        Func<ChartPreparationContext, CancellationToken, ValueTask<ChartPreparationResult>> process);
    IDisposable Register(HookDescriptor descriptor, ChartPreparationStage stage, ChartPreprocessorReads reads,
        Func<ChartPreparationContext, CancellationToken, ValueTask<ChartPreparationResult>> process);
}

public enum AssetKind { Music, Image }
public sealed record AssetPreparationContext(AssetKind Kind, string FileName, Func<Stream> OpenRead);
public sealed record PreparedAsset(string Extension, ReadOnlyMemory<byte> Content);
public interface IAssetPreprocessorRegistry
{
    IDisposable Register(HookDescriptor descriptor, AssetKind kind,
        Func<AssetPreparationContext, CancellationToken, ValueTask<PreparedAsset?>> process);
}

public enum CustomFieldKind { Number, Toggle, Text, Choice }
public sealed record CustomEventField(string Key, string TitleKey, CustomFieldKind Kind, JsonElement Default,
    float Minimum = 0, float Maximum = 1, IReadOnlyList<string>? Choices = null);
public sealed record CustomEventSchema(string TitleKey, int Version, IReadOnlyList<CustomEventField> Fields,
    Func<int, JsonElement, JsonElement>? Migrate = null);
public sealed record CustomChartEvent(string Type, int Floor, double Time, JsonElement Payload, int SchemaVersion = 1,
    IReadOnlyDictionary<string, JsonElement>? PreparedData = null);
public sealed record TimedChartEvent(double Start, double End, JsonElement Payload);
public interface ICustomEventRegistry
{
    IDisposable Register(HookDescriptor descriptor,
        Func<CustomChartEvent, TimedChartEvent?> compile,
        Func<TimedChartEvent, RenderFrame, FrameEffect> evaluate, CustomEventSchema? schema = null);
    IDisposable RegisterCompatibilityConverter(HookDescriptor descriptor,
        Func<CompatibilityEvent, CancellationToken, ValueTask<CompatibilityConversion?>> convert);
}

public sealed record CompatibilityEvent(string Type, int Floor, double AngleOffsetDegrees, int SchemaVersion, JsonElement Payload)
{
    public double Bpm { get; init; }
}
public sealed record CompatibilityConversion(IReadOnlyList<AddChartEvent> Actions,
    IReadOnlyList<AddChartEvent>? Decorations = null, string? Note = null);

public enum RenderStage { PrepareFrame, BackgroundAfterNative, SceneAfterNative }
public readonly record struct RenderFrame(double Time, int Width, int Height, bool Scene3D, UiHost Host);
public readonly record struct FrameEffect(Vector4 Multiply, Vector4 Add)
{
    public static FrameEffect Identity => new(Vector4.One, Vector4.Zero);
}

public interface IRenderPassRegistry
{
    IDisposable Register(HookDescriptor descriptor, RenderStage stage, Func<RenderFrame, FrameEffect> sample);
    IDisposable RegisterShader(HookDescriptor descriptor, RenderStage stage, RenderShader shader,
        Func<RenderFrame, Vector4>? parameters = null, Func<RenderFrame, bool>? enabled = null);
}

/// <summary>Defines a GLSL modEffect(vec2 uv, vec4 color) function for a full-resolution pass.</summary>
public sealed record RenderShader(string Source, bool ProducesAuxiliary = false, IReadOnlyList<string>? Inputs = null)
{
    public bool UsesSceneDepth { get; init; }
    public bool UsesHistory { get; init; }
}

public sealed record OverlayText(Vector2 NormalizedPosition, string Text, uint Color = uint.MaxValue);
public interface IOverlayRegistry
{
    IDisposable Register(HookDescriptor descriptor, Func<RenderFrame, IReadOnlyList<OverlayText>> sample);
}

public enum AudioBus { Music, Hitsounds, Mix }
public readonly record struct AudioFormat(int SampleRate, int Channels);
public delegate void AudioProcessor(AudioFormat format, Span<short> samples);
public interface IAudioProcessorRegistry
{
    IDisposable Register(HookDescriptor descriptor, AudioBus bus, AudioProcessor process);
    IDisposable RegisterRealtime(HookDescriptor descriptor, AudioBus bus, Func<IRealtimeAudioProcessor> create);
}

public readonly record struct RealtimeAudioBlock(AudioFormat Format, long FirstFrame, bool Offline);
public interface IRealtimeAudioProcessor : IDisposable
{
    void Reset(AudioFormat format, long firstFrame);
    void Process(in RealtimeAudioBlock block, Span<float> interleavedSamples);
}

public sealed record ExportArtifact(string TemporaryPath, string OutputFileName);
public sealed record ExportJobDescriptor(HookDescriptor Hook, string TitleKey, string Extension);
public sealed record ExportJobContext(ChartView Chart, string TemporaryPath, IProgress<float> Progress);
public interface IExportJobRegistry
{
    IDisposable Register(ExportJobDescriptor descriptor,
        Func<ExportJobContext, CancellationToken, ValueTask> run);
}

public interface IExportPostprocessorRegistry
{
    IDisposable Register(HookDescriptor descriptor,
        Func<ExportArtifact, CancellationToken, ValueTask> process);
}
