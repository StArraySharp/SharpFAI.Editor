using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SharpFAI.ModApi;

using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor.Mods;

internal sealed partial class ModScope(ModHost host, ModActivation owner) : IModContext, IEditorCommands,
    IMenuRegistry, IToolWindowRegistry, IChartPreprocessorRegistry, IAssetPreprocessorRegistry,
    ICustomEventRegistry, IRenderPassRegistry, IOverlayRegistry, IAudioProcessorRegistry,
    IExportJobRegistry, IExportPostprocessorRegistry, IPlaybackEvents, IMainWindowService, IInspectorRegistry, IModServices
{
    private readonly Dictionary<string, Dictionary<string, string>> _translations = new(StringComparer.OrdinalIgnoreCase);
    private int _nextSubscription;
    internal List<ModRegistration> Registrations { get; } = [];
    internal bool IsActivating { get; set; } = true;

    public string Id => owner.Package.Manifest.Id;
    public string Version => owner.Package.Manifest.Version;
    public UiHost Host => host.Target;
    public string DataDirectory => host.GetDataDirectory(Id);

    public Stream OpenAsset(string path) => File.OpenRead(ModPackage.Resolve(Path.Combine(owner.Shadow, "assets"), path));

    public string Text(string key, string? fallback = null)
    {
        CultureInfo culture = host.Culture();
        return Lookup(culture.Name) ?? Lookup(culture.Parent.Name) ?? Lookup("en") ?? fallback ?? key;

        string? Lookup(string name)
        {
            if (name.Length == 0) return null;
            if (owner.Package.Manifest.Translations?.TryGetValue(name, out var embedded) == true &&
                embedded.TryGetValue(key, out var translation))
                return translation;
            lock (_translations)
            {
                if (!_translations.TryGetValue(name, out var values))
                {
                    string path = ModPackage.Resolve(Path.Combine(owner.Shadow, "locales"), name + ".json");
                    values = File.Exists(path)
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
                    _translations.Add(name, values);
                }
                return values.GetValueOrDefault(key);
            }
        }
    }

    public void Log(string message) => host.Log(Id, message);

    public T? GetService<T>() where T : class
    {
        Type service = typeof(T);
        if (Host != UiHost.Editor || !service.IsInterface || service.Assembly != typeof(IModContext).Assembly ||
            service == typeof(IModContext))
            return null;
        return this as T ?? host.ExternalService?.Invoke(service) as T;
    }

    private IDisposable Add<T>(HookDescriptor descriptor, T value)
    {
        if (!IsActivating)
            throw new InvalidOperationException("Register contributions during ActivateAsync.");
        if (!descriptor.Id.StartsWith(Id + "/", StringComparison.Ordinal) || descriptor.Id.Length <= Id.Length + 1)
            throw new InvalidOperationException($"'{descriptor.Id}' must be in the '{Id}/' namespace.");
        if (Registrations.Any(item => item.Descriptor.Id == descriptor.Id && item.GetType() == typeof(ModRegistration<T>)))
            throw new InvalidOperationException($"Duplicate contribution ID '{descriptor.Id}'.");
        var entry = new ModRegistration<T>(owner, descriptor, value);
        Registrations.Add(entry);
        return entry;
    }

    IDisposable IEditorCommands.Register(CommandDescriptor descriptor, ModCommand handler) =>
        Add(new(descriptor.Id, UiHost.Editor), new RegisteredCommand(descriptor, handler));
    public IDisposable RegisterMenuBarMenu(MenuBarMenuDescriptor descriptor) => Add(new(descriptor.Id, descriptor.Hosts, descriptor.Order), descriptor);
    public IDisposable RegisterMenuBarCommand(MenuBarCommandDescriptor descriptor) => Add(new(descriptor.Id, descriptor.Hosts, descriptor.Order), descriptor);
    public IDisposable RegisterItem(MenuContribution contribution) => Add(new(contribution.Id, UiHost.Editor, contribution.Order),
        contribution with { Submenu = contribution.Submenu?.ToArray() });
    public IDisposable Register(ToolWindowDescriptor descriptor, Func<IToolWindow> create) =>
        Add(new(descriptor.Id.Value, descriptor.Hosts), new RegisteredWindow(descriptor, create));
    public ToolWindowState GetState(ToolWindowId id) => OwnsWindow(id) ? host.WindowOperations?.State(id) ?? default : default;
    public ValueTask<WindowResult> OpenAsync(ToolWindowId id, CancellationToken cancellationToken) =>
        OwnsWindow(id) && host.WindowOperations is { } windows
            ? windows.OpenAsync(id, cancellationToken) : ValueTask.FromResult(WindowResult.Unavailable);
    public ValueTask<WindowResult> CloseAsync(ToolWindowId id, CancellationToken cancellationToken) =>
        OwnsWindow(id) && host.WindowOperations is { } windows
            ? windows.CloseAsync(id, cancellationToken) : ValueTask.FromResult(WindowResult.Unavailable);
    private bool OwnsWindow(ToolWindowId id) => !IsActivating && !owner.Stopping &&
        id.Value is { } value && value.StartsWith(Id + "/", StringComparison.Ordinal);

    public IDisposable Register(HookDescriptor descriptor, ChartPreparationStage stage,
        Func<ChartPreparationContext, CancellationToken, ValueTask<ChartPreparationResult>> process) =>
        Add(descriptor, new ChartProcessor(stage, process));
    public IDisposable Register(HookDescriptor descriptor, ChartPreparationStage stage, ChartPreprocessorReads reads,
        Func<ChartPreparationContext, CancellationToken, ValueTask<ChartPreparationResult>> process) =>
        Add(descriptor, new ChartProcessor(stage, process, reads with
        {
            RootFields = reads.RootFields.ToArray(),
            SettingKeys = reads.SettingKeys?.ToArray(),
        }));
    public IDisposable Register(HookDescriptor descriptor, AssetKind kind,
        Func<AssetPreparationContext, CancellationToken, ValueTask<PreparedAsset?>> process) =>
        Add(descriptor, new AssetProcessor(kind, process));
    public IDisposable Register(HookDescriptor descriptor, Func<CustomChartEvent, TimedChartEvent?> compile,
        Func<TimedChartEvent, RenderFrame, FrameEffect> evaluate, CustomEventSchema? schema = null)
    {
        if (schema is { } value && (value.Version < 1 ||
            value.Fields.Any(field => string.IsNullOrEmpty(field.Key) || field.Default.ValueKind == JsonValueKind.Undefined ||
                !float.IsFinite(field.Minimum) || !float.IsFinite(field.Maximum) || field.Minimum > field.Maximum) ||
            value.Fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != value.Fields.Count))
            throw new InvalidOperationException("Invalid Mod event schema.");
        return Add(descriptor, new CustomEventCompiler(compile, evaluate, schema));
    }
    public IDisposable RegisterCompatibilityConverter(HookDescriptor descriptor,
        Func<CompatibilityEvent, CancellationToken, ValueTask<CompatibilityConversion?>> convert) =>
        Add(descriptor, new CompatibilityConverter(convert));
    public IDisposable Register(HookDescriptor descriptor, RenderStage stage, Func<RenderFrame, FrameEffect> sample) =>
        Add(descriptor, new RenderProcessor(stage, sample));
    public IDisposable RegisterShader(HookDescriptor descriptor, RenderStage stage, RenderShader shader,
        Func<RenderFrame, Vector4>? parameters = null, Func<RenderFrame, bool>? enabled = null)
    {
        if (shader.Source is not { Length: > 0 and <= 65_536 } || shader.Inputs?.Count > 4)
            throw new InvalidOperationException("Invalid Mod shader source size or input count.");
        if (shader.UsesSceneDepth && stage != RenderStage.SceneAfterNative)
            throw new InvalidOperationException("Scene depth is available after the native scene pass.");
        string[] inputs = shader.Inputs?.ToArray() ?? [];
        HookDescriptor ordered = descriptor with { After = (descriptor.After ?? []).Concat(inputs).Distinct().ToArray() };
        return Add(ordered, new RenderProcessor(stage, null, shader with { Inputs = inputs }, parameters, enabled));
    }
    public IDisposable Register(HookDescriptor descriptor, Func<RenderFrame, IReadOnlyList<OverlayText>> sample) =>
        Add(descriptor, sample);
    public IDisposable Register(HookDescriptor descriptor, AudioBus bus, AudioProcessor process) =>
        Add(descriptor, new AudioHook(bus, process));
    public IDisposable RegisterRealtime(HookDescriptor descriptor, AudioBus bus, Func<IRealtimeAudioProcessor> create) =>
        Add(descriptor, new RealtimeAudioHook(bus, create));
    public IDisposable Register(HookDescriptor descriptor, Func<ExportArtifact, CancellationToken, ValueTask> process) =>
        Add(descriptor, process);
    public IDisposable Register(ExportJobDescriptor descriptor, Func<ExportJobContext, CancellationToken, ValueTask> run)
    {
        if (descriptor.Extension.Length is < 1 or > 12 || !descriptor.Extension.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("An export job must declare a file extension.");
        return Add(descriptor.Hook, new CustomExportJob(descriptor, run));
    }
    public IDisposable Subscribe(Action<PlaybackState> callback) =>
        Add(new($"{Id}/observation.{_nextSubscription++}", UiHost.Interactive), new PlaybackObserver(callback));
    public IDisposable SubscribeJudgments(Action<PlaybackJudgment> callback) =>
        Add(new($"{Id}/judgment.{_nextSubscription++}", UiHost.Interactive), new JudgmentObserver(callback));
    public IDisposable Register(HookDescriptor descriptor, Func<CommandContext, IReadOnlyList<InspectorRow>> fields) =>
        Add(descriptor, new InspectorFields(fields));
    public MainWindowState State => MainWindow?.State ?? new(default, default, 1, false, false);
    public IDisposable Subscribe(Action<MainWindowState> callback) =>
        Add(new($"{Id}/window-observation.{_nextSubscription++}", UiHost.Interactive), new MainWindowObserver(callback));
    public ValueTask<bool> RequestFullscreenAsync(bool fullscreen, CancellationToken cancellationToken) =>
        !IsActivating && !owner.Stopping && MainWindow is { } window
            ? window.RequestFullscreenAsync(fullscreen, cancellationToken) : ValueTask.FromResult(false);
    private IMainWindowService? MainWindow => host.ExternalService?.Invoke(typeof(IMainWindowService)) as IMainWindowService;
    public IDisposable Publish(string id, Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler) =>
        Add(new(id), new MessageEndpoint(handler));

    public async ValueTask<JsonElement> InvokeAsync(string id, JsonElement request, CancellationToken cancellationToken)
    {
        string dependencyId = id.Split('/')[0];
        if (dependencyId != Id && !(owner.Package.Manifest.Dependencies ?? []).Any(dependency => dependency.Id == dependencyId))
            throw new InvalidOperationException($"'{dependencyId}' is not a declared dependency.");
        var entry = (IsActivating ? owner.ActivatingDependencies : host.Entries<MessageEndpoint>(Host))
            .FirstOrDefault(candidate => candidate.Descriptor.Id == id);
        using var lease = entry?.Acquire() ?? throw new InvalidOperationException($"Mod service '{id}' is unavailable.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Token, entry.Lifetime);
        JsonElement result = await entry.Value.Invoke(request, linked.Token).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Undefined ? default : result.Clone();
    }

    internal void Validate()
    {
        var commands = Registrations.OfType<ModRegistration<RegisteredCommand>>().Select(entry => entry.Descriptor.Id).ToHashSet();
        var menus = Registrations.OfType<ModRegistration<MenuBarMenuDescriptor>>().Select(entry => entry.Descriptor.Id).ToHashSet();
        foreach (var item in Registrations.OfType<ModRegistration<MenuContribution>>())
            if (!commands.Contains(item.Value.CommandId) ||
                (item.Value.Target.MenuId is { } id && !menus.Contains(id)) ||
                (item.Value.Target.MenuId is null) == (item.Value.Target.Slot is null))
                throw new InvalidOperationException($"Menu '{item.Descriptor.Id}' has an unknown command or destination.");
        foreach (var item in Registrations.OfType<ModRegistration<MenuBarCommandDescriptor>>())
            if (!commands.Contains(item.Value.CommandId))
                throw new InvalidOperationException($"Menu '{item.Descriptor.Id}' has an unknown command.");
    }
}
