using System.Text.Json;
using System.Numerics;
using SharpFAI.ModApi;

namespace SharpFAI.Player.ModSupport;

public sealed record RegisteredCommand(CommandDescriptor Descriptor, ModCommand Run);
public sealed record RegisteredWindow(ToolWindowDescriptor Descriptor, Func<IToolWindow> Create);
public sealed record ChartProcessor(ChartPreparationStage Stage, Func<ChartPreparationContext, CancellationToken, ValueTask<ChartPreparationResult>> Run,
    ChartPreprocessorReads? Reads = null);
public sealed record AssetProcessor(AssetKind Kind, Func<AssetPreparationContext, CancellationToken, ValueTask<PreparedAsset?>> Run);
public sealed record CustomEventCompiler(Func<CustomChartEvent, TimedChartEvent?> Compile,
    Func<TimedChartEvent, RenderFrame, FrameEffect> Evaluate, CustomEventSchema? Schema);
public sealed record CompatibilityConverter(Func<CompatibilityEvent, CancellationToken, ValueTask<CompatibilityConversion?>> Run);
public sealed record RenderProcessor(RenderStage Stage, Func<RenderFrame, FrameEffect>? Sample,
    RenderShader? Shader = null, Func<RenderFrame, Vector4>? Parameters = null, Func<RenderFrame, bool>? Enabled = null);
public sealed record AudioHook(AudioBus Bus, AudioProcessor Process);
public sealed record RealtimeAudioHook(AudioBus Bus, Func<IRealtimeAudioProcessor> Create);
public sealed record CustomExportJob(ExportJobDescriptor Descriptor, Func<ExportJobContext, CancellationToken, ValueTask> Run);
public sealed record MessageEndpoint(Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Invoke);
public sealed record PlaybackObserver(Action<PlaybackState> Notify);
public sealed record JudgmentObserver(Action<PlaybackJudgment> Notify);
public sealed record InspectorFields(Func<CommandContext, IReadOnlyList<InspectorRow>> Build);
public sealed record MainWindowObserver(Action<MainWindowState> Notify);

public abstract class ModRegistration : IDisposable
{
    private int _removed;
    internal ModRegistration(IModRegistrationOwner owner, HookDescriptor descriptor)
    {
        Owner = owner;
        Descriptor = descriptor;
    }

    internal IModRegistrationOwner Owner { get; }
    public string ModId => Owner.Id;
    public HookDescriptor Descriptor { get; }
    public bool IsRegistered => Volatile.Read(ref _removed) == 0 && !Owner.Stopping;
    public CancellationToken Lifetime => Owner.Lifetime;
    public string Text(string key, string? fallback = null) => Owner.Text(key, fallback);
    public void Report(Exception exception) => Owner.Log($"{Descriptor.Id}: {exception.GetBaseException().Message}");
    public IDisposable? Acquire() => IsRegistered ? Owner.Acquire() : null;
    public void Dispose() => Interlocked.Exchange(ref _removed, 1);
}

public sealed class ModRegistration<T> : ModRegistration
{
    internal ModRegistration(IModRegistrationOwner owner, HookDescriptor descriptor, T value) : base(owner, descriptor) =>
        Value = value;
    public T Value { get; }
}

public interface IModWindowOperations
{
    ToolWindowState State(ToolWindowId id);
    ValueTask<WindowResult> OpenAsync(ToolWindowId id, CancellationToken cancellationToken);
    ValueTask<WindowResult> CloseAsync(ToolWindowId id, CancellationToken cancellationToken);
}
