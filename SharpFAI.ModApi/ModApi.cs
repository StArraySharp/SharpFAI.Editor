using System.Text.Json;

namespace SharpFAI.ModApi;

[Flags]
public enum UiHost { Editor = 1, Player = 2, Export = 4, Interactive = Editor | Player, All = Interactive | Export }

public interface ISharpFaiMod : IAsyncDisposable
{
    ValueTask ActivateAsync(IModContext context, CancellationToken cancellationToken);
}

public interface IModContext
{
    string Id { get; }
    string Version { get; }
    UiHost Host { get; }
    string DataDirectory { get; }
    string Text(string key, string? fallback = null);
    Stream OpenAsset(string path);
    void Log(string message);

    /// <summary>Returns a host service, or null if the service is unavailable.</summary>
    T? GetService<T>() where T : class;

    /// <summary>Returns a host service, or throws if the service is unavailable.</summary>
    T GetRequiredService<T>() where T : class => GetService<T>() ?? throw new InvalidOperationException(
        $"Mod '{Id}' requires the unavailable service '{typeof(T).Name}'.");
}

public interface IModServices
{
    IDisposable Publish(string id, Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler);
    ValueTask<JsonElement> InvokeAsync(string id, JsonElement request, CancellationToken cancellationToken);
}

public sealed record ChartRevision(Guid Document, int Version);
public sealed record ChartView(ChartRevision Revision, string? FileName, int LastFloor,
    int SelectedFloor, Func<int, double?> ReadAngle, Func<string, JsonElement?> ReadSetting,
    Func<int, IReadOnlyList<JsonElement>> ReadEvents);

public enum PlaybackMode { Edit, Preview, Auto, Manual, Export }
public sealed record PlaybackState(PlaybackMode Mode, double Time, double Duration, int Floor, bool Playing);
public sealed record CommandContext(ChartView? Chart, PlaybackState Playback);
public sealed record CommandState(bool Enabled = true, bool Checked = false, bool Visible = true);
public sealed record CommandDescriptor(string Id, string TitleKey, string? Shortcut = null,
    Func<CommandContext, CommandState>? State = null, bool SnapshotChart = true);
public delegate ValueTask ModCommand(CommandContext context, CancellationToken cancellationToken);

public interface IEditorCommands
{
    IDisposable Register(CommandDescriptor descriptor, ModCommand handler);
}

public interface IPlayerCommands
{
    IDisposable Register(CommandDescriptor descriptor, ModCommand handler);
}

public enum MenuSlot { File, Edit, View, Chart, Render, Tools, Help, TileContext, DecorationContext, TimelineContext }
public enum MenuBarSlot { Extensions }

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class MenuItemAttribute(string path) : Attribute
{
    public string Path { get; } = path;
    public string? Shortcut { get; init; }
    public int Order { get; init; }
    public bool SnapshotChart { get; init; } = true;
}

public sealed record MenuTarget(MenuSlot? Slot, string? MenuId)
{
    public static MenuTarget BuiltIn(MenuSlot slot) => new(slot, null);
    public static MenuTarget ModMenu(string menuId) => new(null, menuId);
}

public sealed record MenuBarMenuDescriptor(string Id, string TitleKey, MenuBarSlot Slot, UiHost Hosts, int Order = 0);
public sealed record MenuBarCommandDescriptor(string Id, string CommandId, MenuBarSlot Slot, UiHost Hosts, int Order = 0);
public sealed record MenuContribution(string Id, MenuTarget Target, string CommandId, int Order = 0)
{
    public IReadOnlyList<string>? Submenu { get; init; }
}

public interface IMenuRegistry
{
    IDisposable RegisterMenuBarMenu(MenuBarMenuDescriptor descriptor);
    IDisposable RegisterMenuBarCommand(MenuBarCommandDescriptor descriptor);
    IDisposable RegisterItem(MenuContribution contribution);
}

public enum ToolWindowKind { Dockable, Floating, Modal }
public enum ToolWindowScope { Application, Document }
public enum DockSlot { Left, Right, Bottom, Center }
public readonly record struct ToolWindowId(string Value);
public readonly record struct UiSize(float Width, float Height);
public sealed record ToolWindowDescriptor(ToolWindowId Id, string TitleKey, UiHost Hosts, ToolWindowKind Kind,
    ToolWindowScope Scope, DockSlot? DefaultDock = null, bool ShowInWindowMenu = true, UiSize? MinimumSize = null);
public readonly record struct ToolWindowState(bool Open, bool Visible, bool Focused);
public enum WindowResult { Unavailable, Opened, Focused, Closed }
public sealed record ToolWindowContext(CommandContext Command, bool Focused);
public sealed record UiAction(string Id, JsonElement? Value = null);
public abstract record UiNode;
public sealed record UiText(string Value, bool Muted = false) : UiNode;
public sealed record UiButton(string Id, string Label, bool Enabled = true) : UiNode;
public sealed record UiToggle(string Id, string Label, bool Value) : UiNode;
public sealed record UiNumber(string Id, string Label, float Value, float Minimum, float Maximum) : UiNode;
public sealed record UiChoice(string Id, string Label, int Selected, IReadOnlyList<string> Options) : UiNode;
public sealed record UiProgress(float Fraction, string? Label = null) : UiNode;
public sealed record UiGroup(string Id, string Label, IReadOnlyList<UiNode> Children) : UiNode;
public sealed record UiTable(string Id, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) : UiNode;

public interface IToolWindowRegistry
{
    IDisposable Register(ToolWindowDescriptor descriptor, Func<IToolWindow> create);
    ToolWindowState GetState(ToolWindowId id);
    ValueTask<WindowResult> OpenAsync(ToolWindowId id, CancellationToken cancellationToken);
    ValueTask<WindowResult> CloseAsync(ToolWindowId id, CancellationToken cancellationToken);
}

public interface IToolWindow : IAsyncDisposable
{
    IReadOnlyList<UiNode> Build(in ToolWindowContext context);
    ValueTask HandleAsync(UiAction action, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public interface IUiDialogs
{
    ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
    ValueTask<string?> OpenFileAsync(string description, string extensions, CancellationToken cancellationToken);
}

public record MainWindowState(UiSize Size, UiSize FramebufferSize, float Scale, bool Focused, bool Fullscreen);
public interface IMainWindowService
{
    MainWindowState State { get; }
    IDisposable Subscribe(Action<MainWindowState> callback);
    ValueTask<bool> RequestFullscreenAsync(bool fullscreen, CancellationToken cancellationToken);
}

public enum EditResult { Invalid, Applied, RevisionConflict }
public abstract record ChartOperation;
public sealed record InsertAngles(int AfterFloor, IReadOnlyList<double> Angles) : ChartOperation;
public sealed record DeleteAngles(int FirstFloor, int Count) : ChartOperation;
public sealed record SetChartSetting(string Key, JsonElement Value) : ChartOperation;
public sealed record AddChartEvent(int Floor, string Type, JsonElement Fields) : ChartOperation;
public sealed record UpdateChartEvent(int Floor, int Index, JsonElement Fields) : ChartOperation;
public sealed record RemoveChartEvent(int Floor, int Index) : ChartOperation;
public sealed record ChartEditRequest(string Description, IReadOnlyList<ChartOperation> Operations);

public interface IChartEdits
{
    ValueTask<EditResult> CommitAsync(ChartRevision expected, ChartEditRequest edit, CancellationToken cancellationToken);
}

public sealed record InspectorRow(string Label, string Value, string? CommandId = null);
public interface IInspectorRegistry
{
    IDisposable Register(HookDescriptor descriptor, Func<CommandContext, IReadOnlyList<InspectorRow>> fields);
}

public sealed record PlaybackJudgment(int Floor, double Time, double OffsetDegrees, string Margin, bool Accepted, bool Failed);
public interface IPlaybackEvents
{
    IDisposable Subscribe(Action<PlaybackState> callback);
    IDisposable SubscribeJudgments(Action<PlaybackJudgment> callback);
}

public enum TransportAction { Play, Pause, Seek }
public interface IPlaybackControl
{
    ValueTask<bool> RequestAsync(TransportAction action, double time, CancellationToken cancellationToken);
}
