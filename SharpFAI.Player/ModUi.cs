using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ImGui4NET;
using NativeFileDialog;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;
using ModRuntime = SharpFAI.Player.ModSupport.IModRuntime;

namespace SharpFAI.Player;

internal sealed class ModUi : IModWindowOperations, IUiDialogs, IMainWindowService, IChartEdits, IPlaybackControl
{
    private sealed class Window(ModRegistration<RegisteredWindow> entry, IToolWindow instance, IDisposable lease)
    {
        public ModRegistration<RegisteredWindow> Entry { get; } = entry;
        public IToolWindow Instance { get; } = instance;
        public IDisposable Lease { get; } = lease;
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime);
        public Task? Action { get; set; }
        public bool Focus { get; set; } = true;
        public ToolWindowState State { get; set; } = new(true, false, false);
    }

    private sealed record PendingDialog(string Title, string Message, CancellationToken Cancellation, TaskCompletionSource<bool> Completion);
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<(ToolWindowId, Guid?), Window> _windows = [];
    private readonly ConcurrentDictionary<(ToolWindowId, Guid?), ToolWindowState> _windowStates = new();
    private readonly Dictionary<string, Task> _commands = [];
    private readonly Dictionary<DockSlot, uint> _docks = [];
    private readonly Queue<PendingDialog> _dialogs = new();
    private readonly List<string> _closingModals = [];
    private readonly AsyncLocal<Guid?> _commandDocument = new();
    private readonly Func<CommandContext> _snapshot;
    private readonly Func<CommandContext> _commandSnapshot;
    private readonly Func<ChartRevision, ChartEditRequest, EditResult>? _edit;
    private readonly Func<TransportAction, double, bool> _transport;
    private readonly Action<bool> _fullscreen;
    private readonly string _layoutFile;
    private readonly string _shortcutFile;
    private readonly HashSet<string> _remembered;
    private readonly Dictionary<string, string> _shortcuts;
    private string? _recordShortcut;
    private Guid? _document;
    private bool _restore = true, _shuttingDown, _hide, _dialogOpened;
    private int _generation = -1;
    private long _observedAt;
    private PlaybackState? _lastPlayback;
    private Task? _operation;
    private Task? _customExport;
    private CancellationTokenSource? _customExportCancellation;
    private readonly ExportProgress _exportProgress = new();
    private string? _status;
    private IReadOnlyList<ModNotice> _displayedNotices = [];
    private byte[] _logText = [];
    private (string Title, Func<Task> Run)? _confirmation;
    private PendingDialog? _dialog;

    public ModUi(ModRuntime host, Func<CommandContext> snapshot, Action<bool> fullscreen,
        Func<TransportAction, double, bool> transport, Func<ChartRevision, ChartEditRequest, EditResult>? edit = null,
        Func<CommandContext>? commandSnapshot = null)
    {
        Host = host;
        _snapshot = snapshot;
        _commandSnapshot = commandSnapshot ?? snapshot;
        _transport = transport;
        _fullscreen = fullscreen;
        _edit = edit;
        host.WindowOperations = this;
        host.ExternalService = type => type == typeof(IUiDialogs) || type == typeof(IMainWindowService) ||
            type == typeof(IChartEdits) && _edit is not null || type == typeof(IPlaybackControl) ? this : null;
        _layoutFile = Path.Combine(host.Root, $"windows-{host.Target.ToString().ToLowerInvariant()}.json");
        _shortcutFile = Path.Combine(host.Root, $"shortcuts-{host.Target.ToString().ToLowerInvariant()}.json");
        _remembered = ReadSaved<HashSet<string>>(_layoutFile) ?? [];
        _shortcuts = ReadSaved<Dictionary<string, string>>(_shortcutFile) ?? [];
    }

    public ModRuntime Host { get; }
    public MainWindowState State { get; private set; } = new(default, default, 1, false, false);
    public bool HasFocus { get; private set; }
    public void Initialize() => Start(Host.InitializeAsync);

    public void SetDocks(uint left, uint right, uint bottom, uint center)
    {
        _docks[DockSlot.Left] = left;
        _docks[DockSlot.Right] = right;
        _docks[DockSlot.Bottom] = bottom;
        _docks[DockSlot.Center] = center;
    }

    public void Tick(MainWindowState state, bool hidden)
    {
        _hide = hidden;
        if (_operation is { IsCompleted: true } completed)
        {
            _operation = null;
            _status = completed.Exception?.GetBaseException().Message;
        }
        if (_customExport is { IsCompleted: true } export)
        {
            _customExport = null;
            _customExportCancellation?.Dispose();
            _customExportCancellation = null;
            _status = export.Exception?.GetBaseException().Message;
        }
        if (Host.Generation != _generation || _windows.Count != 0 || !_queue.IsEmpty)
        {
            Guid? document = _snapshot().Chart?.Revision.Document;
            foreach (var pair in _windows.ToArray())
                if (!pair.Value.Entry.IsRegistered || pair.Key.Item2 is not null && pair.Key.Item2 != document ||
                    !Host.Entries<RegisteredWindow>(Host.Target).Contains(pair.Value.Entry))
                    Close(pair.Key, remember: false);
            if (_generation != Host.Generation || _document != document || _restore)
            {
                _generation = Host.Generation;
                _document = document;
                foreach (var item in Host.Entries<RegisteredWindow>(Host.Target))
                {
                    var descriptor = item.Value.Descriptor;
                    if (descriptor.Kind != ToolWindowKind.Modal && _remembered.Contains(descriptor.Id.Value))
                        Open(descriptor.Id, descriptor.Scope == ToolWindowScope.Document ? document : null, focus: false);
                }
                _restore = false;
            }
        }
        if (state != State)
        {
            State = state;
            foreach (var entry in Host.Entries<MainWindowObserver>(Host.Target))
            {
                using var lease = entry.Acquire();
                if (lease is null) continue;
                try { entry.Value.Notify(state); }
                catch (Exception error) { entry.Report(error); }
            }
        }
        while (_queue.TryDequeue(out var action)) action();
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        var entries = Host.Entries<PlaybackObserver>(Host.Target);
        if (entries.Count == 0) return;
        var current = _snapshot().Playback;
        long now = Environment.TickCount64;
        if (current == _lastPlayback || now - _observedAt < 30 &&
            current.Mode == _lastPlayback?.Mode && current.Playing == _lastPlayback?.Playing)
            return;
        _lastPlayback = current;
        _observedAt = now;
        foreach (var entry in entries)
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            try { entry.Value.Notify(current); }
            catch (Exception error) { entry.Report(error); }
        }
    }

    public void DrawItems(MenuSlot slot) => DrawItems(MenuTarget.BuiltIn(slot));
    public bool HasItems(MenuSlot slot) => Items(MenuTarget.BuiltIn(slot)).Any();

    public void DrawPlayerRenderMenu()
    {
        if ((Items(MenuTarget.BuiltIn(MenuSlot.Render)).Any() || Host.Entries<CustomExportJob>(Host.Target).Count != 0) &&
            ImGui.BeginMenu(Tr("渲染扩展", "Render extensions")) != 0)
        {
            DrawItems(MenuSlot.Render);
            DrawExportItems();
            ImGui.EndMenu();
        }
    }

    public void DrawExportItems()
    {
        var entries = Host.Entries<CustomExportJob>(Host.Target);
        if (entries.Count == 0 && _customExport is null) return;
        if (_customExport is not null)
        {
            ImGui.ProgressBar(_exportProgress.Value, Vec(160, 0));
            if (ImGui.MenuItem(Tr("取消 Mod 导出", "Cancel Mod export")) != 0) _customExportCancellation?.Cancel();
            return;
        }
        bool chart = _snapshot().Chart is not null;
        foreach (var entry in entries)
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            if (ImGui.MenuItem(Label(entry.Text(entry.Value.Descriptor.TitleKey), entry.Descriptor.Id),
                    null, 0, (byte)(chart ? 1 : 0)) != 0)
            {
                Guid? document = _snapshot().Chart?.Revision.Document;
                _queue.Enqueue(() => RunExport(entry, document));
            }
        }
    }

    private void RunExport(ModRegistration<CustomExportJob> entry, Guid? document)
    {
        if (_shuttingDown || _customExport is not null || _snapshot().Chart?.Revision.Document != document)
            return;
        var lease = entry.Acquire();
        if (lease is null) return;
        try
        {
            var job = entry.Value;
            string title = entry.Text(job.Descriptor.TitleKey);
            string extension = job.Descriptor.Extension;
            string? output = NativeFileDialog.NativeFileDialog.GetFileDialog()?.SaveFile(title, "", "report." + extension,
                new SaveFileFilter { Filter = new() { [title] = extension } });
            if (string.IsNullOrWhiteSpace(output) || _commandSnapshot().Chart is not { } chart)
            {
                lease.Dispose();
                return;
            }
            string destination = Path.GetFullPath(Path.HasExtension(output) ? output : output + "." + extension);
            string staging = Path.Combine(Path.GetDirectoryName(destination)!,
                $".sharpfai-mod-export-{Guid.NewGuid():N}.{extension}");
            var cancellation = _customExportCancellation = CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime);
            _exportProgress.Report(0);
            _customExport = Task.Run(async () =>
            {
                using (lease)
                {
                    try
                    {
                        await job.Run(new(chart, staging, _exportProgress), cancellation.Token).ConfigureAwait(false);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (!File.Exists(staging)) throw new InvalidDataException("The Mod did not produce an export file.");
                        File.Move(staging, destination, overwrite: true);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        entry.Report(error);
                        throw;
                    }
                    finally { File.Delete(staging); }
                }
            });
        }
        catch (Exception error)
        {
            lease.Dispose();
            entry.Report(error);
        }
    }

    private sealed class ExportProgress : IProgress<float>
    {
        private volatile float _value;
        public float Value => _value;
        public void Report(float value) => _value = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    public void DrawWindowMenu()
    {
        var entries = Host.Entries<RegisteredWindow>(Host.Target);
        if (!entries.Any(entry => entry.IsRegistered && entry.Value.Descriptor.ShowInWindowMenu) ||
            ImGui.BeginMenu(Tr("Mod 窗口", "Mod windows")) == 0)
            return;
        foreach (var entry in entries)
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            var item = entry.Value.Descriptor;
            if (!item.ShowInWindowMenu) continue;
            bool open = ((IModWindowOperations)this).State(item.Id).Open;
            Guid? document = _snapshot().Chart?.Revision.Document;
            if (ImGui.MenuItem(Label(entry.Text(item.TitleKey), item.Id.Value), null, (byte)(open ? 1 : 0),
                    (byte)(item.Scope == ToolWindowScope.Application || document.HasValue ? 1 : 0)) != 0)
                _queue.Enqueue(() => { if (open) Close(Key(item, document)); else Open(item.Id, document); });
        }
        ImGui.EndMenu();
    }

    public void DrawMenuBar()
    {
        var menus = Host.Entries<MenuBarMenuDescriptor>(Host.Target);
        var buttons = Host.Entries<MenuBarCommandDescriptor>(Host.Target);
        var tools = Host.Target == UiHost.Player && Items(MenuTarget.BuiltIn(MenuSlot.Tools)).Any();
        if (menus.Count == 0 && buttons.Count == 0 && !tools) return;
        if (tools && ImGui.BeginMenu("Mod") != 0)
        {
            DrawItems(MenuSlot.Tools);
            ImGui.EndMenu();
        }
        var entries = menus.Cast<ModRegistration>().Concat(buttons).Where(entry => entry.IsRegistered)
            .OrderBy(entry => entry.Descriptor.Order).ThenBy(entry => entry.Descriptor.Id, StringComparer.Ordinal).ToArray();
        int overflow = entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            if (ImGui.GetContentRegionAvail().x < 285)
            {
                overflow = i;
                break;
            }
            DrawEntry(entries[i]);
        }
        if (overflow < entries.Length && ImGui.BeginMenu("Mods…") != 0)
        {
            for (int i = overflow; i < entries.Length; i++) DrawEntry(entries[i]);
            ImGui.EndMenu();
        }

        void DrawEntry(ModRegistration entry)
        {
            if (entry is ModRegistration<MenuBarMenuDescriptor> menu)
            {
                using var lease = entry.Acquire();
                var target = MenuTarget.ModMenu(entry.Descriptor.Id);
                if (lease is not null && Items(target).Any() &&
                    ImGui.BeginMenu(Label(entry.Text(menu.Value.TitleKey), entry.Descriptor.Id)) != 0)
                {
                    DrawItems(target);
                    ImGui.EndMenu();
                }
            }
            else if (entry is ModRegistration<MenuBarCommandDescriptor> button)
                DrawCommand(button.Value.CommandId, entry.Descriptor.Id);
        }
    }

    private IEnumerable<ModRegistration<MenuContribution>> Items(MenuTarget target) =>
        Host.Entries<MenuContribution>(Host.Target).Where(item => item.IsRegistered && item.Value.Target == target);

    private void DrawItems(MenuTarget target) => DrawItems(Items(target), 0);

    private void DrawItems(IEnumerable<ModRegistration<MenuContribution>> items, int depth)
    {
        HashSet<string>? drawnSubmenus = null;
        foreach (var item in items)
        {
            if (item.Value.Submenu is not { } path || path.Count <= depth)
            {
                DrawCommand(item.Value.CommandId, item.Descriptor.Id);
                continue;
            }
            string segment = path[depth];
            if (!(drawnSubmenus ??= new(StringComparer.Ordinal)).Add(segment) ||
                ImGui.BeginMenu(Label(item.Text(segment), $"sharpfai.mod.submenu/{segment}")) == 0)
                continue;
            DrawItems(items.Where(candidate => candidate.Value.Submenu is { } sub && sub.Count > depth && sub[depth] == segment), depth + 1);
            ImGui.EndMenu();
        }
    }

    public unsafe void DrawContext(MenuSlot slot, string popup, Action? selectTarget = null)
    {
        if (!Items(MenuTarget.BuiltIn(slot)).Any()) return;
        if (ImGui.IsItemHovered() != 0 && ImGui.GetIO()->KeyShift != 0 && ImGui.IsMouseReleased(1) != 0)
        {
            selectTarget?.Invoke();
            ImGui.OpenPopup(popup);
        }
        if (ImGui.BeginPopup(popup) == 0) return;
        DrawItems(slot);
        ImGui.EndPopup();
    }

    private void DrawCommand(string id, string itemId)
    {
        var entry = Host.Entries<RegisteredCommand>(Host.Target).FirstOrDefault(candidate => candidate.Descriptor.Id == id);
        using var lease = entry?.Acquire();
        if (entry is null || lease is null) return;
        var context = _snapshot();
        var descriptor = entry.Value.Descriptor;
        if (!TryState(entry, context, out var state) || !state.Visible) return;
        if (ImGui.MenuItem(Label(entry.Text(descriptor.TitleKey), itemId), Shortcut(descriptor),
                (byte)(state.Checked ? 1 : 0), (byte)(state.Enabled && !_commands.ContainsKey(id) ? 1 : 0)) != 0)
            _queue.Enqueue(() => RunCommand(entry, context.Chart?.Revision.Document));
    }

    private string? Shortcut(CommandDescriptor descriptor) =>
        _shortcuts.TryGetValue(descriptor.Id, out string? value) ? value : descriptor.Shortcut;

    public unsafe bool HandleShortcuts(bool blocked)
    {
        var entries = Host.Entries<RegisteredCommand>(Host.Target);
        if (entries.Count == 0) return false;
        if (blocked || _shuttingDown || ImGui.GetIO()->WantTextInput != 0 || ImGui.IsAnyItemActive() != 0 ||
            ImGui.IsPopupOpen((string?)null, (int)(ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupId |
                ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupLevel)) != 0)
            return false;
        if (!TryPressedChord(out var chord)) return false;
        var candidates = entries.Where(entry => entry.IsRegistered &&
            string.Equals(Shortcut(entry.Value.Descriptor), chord, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (candidates.Length != 1) return false;
        var entry = candidates[0];
        using var lease = entry.Acquire();
        if (lease is null) return false;
        var context = _snapshot();
        if (!TryState(entry, context, out var state) || !state.Enabled || !state.Visible) return false;
        _queue.Enqueue(() => RunCommand(entry, context.Chart?.Revision.Document));
        return true;
    }

    private static unsafe bool TryPressedChord(out string chord)
    {
        chord = "";
        var io = ImGui.GetIO();
        bool control = io->KeyCtrl != 0, alt = io->KeyAlt != 0, shift = io->KeyShift != 0, super = io->KeySuper != 0;
        if (control || alt || super)
        {
            for (var key = ImGuiKey.ImGuiKey_A; key <= ImGuiKey.ImGuiKey_Z; key++)
            {
                string name = ((char)('A' + (int)key - (int)ImGuiKey.ImGuiKey_A)).ToString();
                if ((control || super) && "NOSZYQFPCXVDL".Contains(name) ||
                    alt && !control && !super && "FBAHN".Contains(name))
                    continue;
                if (ImGui.IsKeyPressed(key, 0) == 0) continue;
                chord = Prefix() + name;
                return true;
            }
        }
        for (var key = ImGuiKey.ImGuiKey_F1; key <= ImGuiKey.ImGuiKey_F12; key++)
        {
            if ((key == ImGuiKey.ImGuiKey_F4 && alt) || ImGui.IsKeyPressed(key, 0) == 0) continue;
            chord = Prefix() + "F" + ((int)key - (int)ImGuiKey.ImGuiKey_F1 + 1);
            return true;
        }
        return false;

        string Prefix() => (control ? "Ctrl+" : "") + (alt ? "Alt+" : "") +
            (shift ? "Shift+" : "") + (super ? "Super+" : "");
    }

    private static bool TryState(ModRegistration<RegisteredCommand> entry, CommandContext context, out CommandState state)
    {
        try { state = entry.Value.Descriptor.State?.Invoke(context) ?? new(); return true; }
        catch (Exception error) { entry.Report(error); state = new(false); return false; }
    }

    private void RunCommand(ModRegistration<RegisteredCommand> entry, Guid? document)
    {
        var id = entry.Descriptor.Id;
        if (_commands.ContainsKey(id) || !entry.IsRegistered) return;
        var context = _snapshot();
        if (document != context.Chart?.Revision.Document || !TryState(entry, context, out var state) ||
            !state.Enabled || !state.Visible) return;
        Task task = InvokeAsync(entry, entry.Value.Descriptor.SnapshotChart ? _commandSnapshot() : context);
        if (!task.IsCompleted)
        {
            _commands[id] = task;
            _ = task.ContinueWith(_ => _queue.Enqueue(() => _commands.Remove(id)), TaskScheduler.Default);
        }
    }

    public void DrawInspectorFields()
    {
        var entries = Host.Entries<InspectorFields>(Host.Target);
        if (entries.Count == 0) return;
        var context = _snapshot();
        foreach (var entry in entries)
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            try
            {
                foreach (var row in entry.Value.Build(context))
                {
                    ImGui.TextUnformatted($"{row.Label}  {row.Value}");
                    if (row.CommandId is { } command) DrawCommand(command, entry.Descriptor.Id + "/" + command);
                }
            }
            catch (Exception error) { entry.Report(error); }
        }
    }

    private async Task InvokeAsync(ModRegistration<RegisteredCommand> entry, CommandContext context)
    {
        using var lease = entry.Acquire();
        if (lease is null) return;
        Guid? previous = _commandDocument.Value;
        _commandDocument.Value = context.Chart?.Revision.Document;
        try { await entry.Value.Run(context, entry.Lifetime).ConfigureAwait(false); }
        catch (OperationCanceledException) when (entry.Lifetime.IsCancellationRequested) { }
        catch (Exception error) { entry.Report(error); }
        finally { _commandDocument.Value = previous; }
    }

    public unsafe void DrawWindows(bool blocked = false)
    {
        HasFocus = false;
        foreach (string popup in _closingModals)
            if (ImGui.BeginPopupModal(popup, 0) != 0)
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }
        _closingModals.Clear();
        if (_hide) return;
        foreach (var pair in _windows.ToArray())
        {
            var window = pair.Value;
            using var drawLease = window.Entry.Acquire();
            if (drawLease is null) continue;
            var descriptor = window.Entry.Value.Descriptor;
            string label = Label(window.Entry.Text(descriptor.TitleKey), "mod.window." + descriptor.Id.Value);
            if (descriptor.DefaultDock is { } dock && _docks.TryGetValue(dock, out uint node))
                ImGui.SetNextWindowDockID(node, (int)ImGuiCond.ImGuiCond_FirstUseEver);
            if (descriptor.MinimumSize is { } size)
                ImGui.SetNextWindowSizeConstraints(Vec(size.Width, size.Height), Vec(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowSize(Vec(440, 330), (int)ImGuiCond.ImGuiCond_FirstUseEver);
            bool open = true;
            bool modal = descriptor.Kind == ToolWindowKind.Modal;
            if (window.Focus)
            {
                if (modal) ImGui.OpenPopup(label);
                else ImGui.SetNextWindowFocus();
                window.Focus = false;
            }
            int flags = Host.Target != UiHost.Editor || descriptor.Kind != ToolWindowKind.Dockable
                ? (int)ImGuiWindowFlags.ImGuiWindowFlags_NoDocking : 0;
            bool visible = (modal ? ImGui.BeginPopupModal(label, ref open, flags) : ImGui.Begin(label, ref open, flags)) != 0;
            bool focused = visible && ImGui.IsWindowFocused(0) != 0;
            HasFocus |= focused;
            window.State = new(open, visible, focused);
            _windowStates[pair.Key] = window.State;
            if (visible)
            {
                try
                {
                    if (blocked || window.Action is { IsCompleted: false }) ImGui.BeginDisabled();
                    try { DrawNodes(window, window.Instance.Build(new(_snapshot(), focused))); }
                    finally { if (blocked || window.Action is { IsCompleted: false }) ImGui.EndDisabled(); }
                }
                catch (Exception error) { window.Entry.Report(error); open = false; }
            }
            if (modal) { if (visible) ImGui.EndPopup(); }
            else ImGui.End();
            if (!open || modal && !visible && ImGui.IsPopupOpen(label) == 0)
            {
                var key = pair.Key;
                _queue.Enqueue(() => Close(key));
            }
        }
        DrawDialog();
    }

    private void DrawNodes(Window window, IReadOnlyList<UiNode> nodes)
    {
        foreach (UiNode node in nodes)
        {
            switch (node)
            {
                case UiText text:
                    if (text.Muted) Muted(text.Value);
                    else ImGui.TextUnformatted(text.Value);
                    break;
                case UiButton button:
                    ImGui.BeginDisabled((byte)(button.Enabled ? 0 : 1));
                    if (ImGui.Button(Label(button.Label, button.Id), default) != 0) QueueAction(window, new(button.Id));
                    ImGui.EndDisabled();
                    break;
                case UiToggle toggle:
                    bool value = toggle.Value;
                    if (ImGui.Checkbox(Label(toggle.Label, toggle.Id), ref value) != 0)
                        QueueAction(window, new(toggle.Id, JsonSerializer.SerializeToElement(value)));
                    break;
                case UiNumber number:
                    float scalar = number.Value;
                    if (ImGui.SliderFloat(Label(number.Label, number.Id), ref scalar, number.Minimum, number.Maximum, "%.3g", 0) != 0)
                        QueueAction(window, new(number.Id, JsonSerializer.SerializeToElement(scalar)));
                    break;
                case UiChoice choice:
                    if (ImGui.BeginCombo(Label(choice.Label, choice.Id), choice.Options.ElementAtOrDefault(choice.Selected) ?? "", 0) != 0)
                    {
                        for (int i = 0; i < choice.Options.Count; i++)
                            if (ImGui.Selectable(Label(choice.Options[i], $"{choice.Id}.{i}"), (byte)(choice.Selected == i ? 1 : 0), 0, default) != 0)
                                QueueAction(window, new(choice.Id, JsonSerializer.SerializeToElement(i)));
                        ImGui.EndCombo();
                    }
                    break;
                case UiProgress progress:
                    ImGui.ProgressBar(Math.Clamp(progress.Fraction, 0, 1), Vec(-1, 0), progress.Label);
                    break;
                case UiGroup group when ImGui.CollapsingHeader(Label(group.Label, group.Id), 0) != 0:
                    DrawNodes(window, group.Children);
                    break;
                case UiTable table when table.Columns.Count is > 0 and <= 64:
                    DrawTable(table);
                    break;
            }
        }
    }

    private static void DrawTable(UiTable table)
    {
        if (ImGui.BeginTable(table.Id, table.Columns.Count, (int)ImGuiTableFlags.ImGuiTableFlags_RowBg, default, 0) == 0) return;
        foreach (string column in table.Columns) ImGui.TableSetupColumn(column, 0, 0, 0);
        ImGui.TableHeadersRow();
        foreach (var row in table.Rows)
        {
            ImGui.TableNextRow(0, 0);
            for (int column = 0; column < Math.Min(table.Columns.Count, row.Count); column++)
                if (ImGui.TableSetColumnIndex(column) != 0) ImGui.TextUnformatted(row[column]);
        }
        ImGui.EndTable();
    }

    private void QueueAction(Window window, UiAction action) => _queue.Enqueue(() =>
    {
        if (!window.Lifetime.IsCancellationRequested && window.Action is not { IsCompleted: false })
            window.Action = HandleAction(window, action);
    });

    private async Task HandleAction(Window window, UiAction action)
    {
        Guid? previous = _commandDocument.Value;
        _commandDocument.Value = _snapshot().Chart?.Revision.Document;
        try { await window.Instance.HandleAsync(action, window.Lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (window.Lifetime.IsCancellationRequested) { }
        catch (Exception error) { window.Entry.Report(error); }
        finally { _commandDocument.Value = previous; }
    }

    private static (ToolWindowId, Guid?) Key(ToolWindowDescriptor descriptor, Guid? document) =>
        (descriptor.Id, descriptor.Scope == ToolWindowScope.Document ? document : null);

    ToolWindowState IModWindowOperations.State(ToolWindowId id) =>
        _windowStates.GetValueOrDefault((id, _commandDocument.Value ?? _document),
            _windowStates.GetValueOrDefault((id, null)));

    ValueTask<WindowResult> IModWindowOperations.OpenAsync(ToolWindowId id, CancellationToken token)
    {
        var document = _commandDocument.Value ?? _document;
        return Dispatch(() => Open(id, document), token);
    }

    ValueTask<WindowResult> IModWindowOperations.CloseAsync(ToolWindowId id, CancellationToken token)
    {
        var document = _commandDocument.Value ?? _document;
        return Dispatch(() =>
        {
            var key = (id, document);
            if (!_windows.ContainsKey(key)) key = (id, null);
            return Close(key) ? WindowResult.Closed : WindowResult.Unavailable;
        }, token);
    }

    private WindowResult Open(ToolWindowId id, Guid? document, bool focus = true)
    {
        var entry = Host.Entries<RegisteredWindow>(Host.Target).FirstOrDefault(candidate => candidate.Value.Descriptor.Id == id);
        if (entry is null || _shuttingDown) return WindowResult.Unavailable;
        var descriptor = entry.Value.Descriptor;
        if (descriptor.Scope == ToolWindowScope.Document && (document is null || document != _snapshot().Chart?.Revision.Document))
            return WindowResult.Unavailable;
        var key = Key(descriptor, document);
        if (_windows.TryGetValue(key, out var current))
        {
            current.Focus |= focus;
            return WindowResult.Focused;
        }
        var lease = entry.Acquire();
        if (lease is null) return WindowResult.Unavailable;
        try
        {
            _windows.Add(key, new(entry, entry.Value.Create(), lease) { Focus = focus });
            _windowStates[key] = new(true, false, false);
            if (descriptor.Kind != ToolWindowKind.Modal && _remembered.Add(id.Value)) SaveLayout();
            return WindowResult.Opened;
        }
        catch (Exception error) { lease.Dispose(); entry.Report(error); return WindowResult.Unavailable; }
    }

    private bool Close((ToolWindowId, Guid?) key, bool remember = true)
    {
        if (!_windows.Remove(key, out var window)) return false;
        _windowStates.TryRemove(key, out _);
        if (window.Entry.Value.Descriptor.Kind == ToolWindowKind.Modal)
            _closingModals.Add("###mod.window." + key.Item1.Value);
        if (remember && _remembered.Remove(key.Item1.Value)) SaveLayout();
        try { window.Lifetime.Cancel(); }
        catch (Exception error) { window.Entry.Report(error); }
        _ = DisposeWindowAsync(window);
        return true;
    }

    private static async Task DisposeWindowAsync(Window window)
    {
        try
        {
            if (window.Action is { } action) await action.ConfigureAwait(false);
            await window.Instance.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) { window.Entry.Report(error); }
        finally { window.Lifetime.Dispose(); window.Lease.Dispose(); }
    }

    private void SaveLayout() => SaveJson(_layoutFile, _remembered);

    private static T? ReadSaved<T>(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : default; }
        catch (Exception) { return default; }
    }

    private void SaveJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Host.Root);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value));
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        catch (Exception error) { _status = error.Message; }
    }

    internal ValueTask<T> Dispatch<T>(Func<T> action, CancellationToken token)
    {
        if (_shuttingDown) return ValueTask.FromResult(default(T)!);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            if (token.IsCancellationRequested) completion.TrySetCanceled(token);
            else
            {
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            }
        });
        return new(completion.Task.WaitAsync(token));
    }

    public void Shutdown()
    {
        _shuttingDown = true;
        _customExportCancellation?.Cancel();
        foreach (var key in _windows.Keys.ToArray()) Close(key, remember: false);
        while (_dialogs.TryDequeue(out var dialog)) dialog.Completion.TrySetResult(false);
        _dialog?.Completion.TrySetResult(false);
        while (_queue.TryDequeue(out var action)) action();
        Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _customExportCancellation?.Dispose();
    }

    public ValueTask<EditResult> CommitAsync(ChartRevision expected, ChartEditRequest edit, CancellationToken token) =>
        Dispatch(() => _edit?.Invoke(expected, edit) ?? EditResult.Invalid, token);

    public ValueTask<bool> RequestAsync(TransportAction action, double time, CancellationToken token) =>
        Dispatch(() => _transport(action, time), token);

    public ValueTask<bool> RequestFullscreenAsync(bool fullscreen, CancellationToken token) =>
        Dispatch(() => { _fullscreen(fullscreen); return true; }, token);

    IDisposable IMainWindowService.Subscribe(Action<MainWindowState> callback) =>
        throw new InvalidOperationException("Subscribe through the scoped Mod context.");

    public async ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = await Dispatch(() =>
        {
            _dialogs.Enqueue(new(title, message, token, completion));
            return true;
        }, token).ConfigureAwait(false);
        return queued && await completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public ValueTask<string?> OpenFileAsync(string description, string extensions, CancellationToken token) =>
        Dispatch(() => ChooseFile(description, extensions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)), token);

    private void DrawDialog()
    {
        if (_dialog is null)
        {
            while (_dialogs.TryDequeue(out var next))
                if (!next.Cancellation.IsCancellationRequested) { _dialog = next; break; }
        }
        if (_dialog is not { } dialog) return;
        string name = Label(dialog.Title, "mod.dialog");
        if (!_dialogOpened) { ImGui.OpenPopup(name); _dialogOpened = true; }
        if (ImGui.BeginPopupModal(name, 0) == 0) return;
        ImGui.TextUnformatted(dialog.Message);
        bool accepted = ImGui.Button(Tr("确认", "OK"), default) != 0;
        ImGui.SameLine();
        if (ImGui.Button(Tr("取消", "Cancel"), default) != 0 || accepted || dialog.Cancellation.IsCancellationRequested)
        {
            dialog.Completion.TrySetResult(accepted && !dialog.Cancellation.IsCancellationRequested);
            _dialogOpened = false;
            _dialog = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    public void DrawSettings(bool acceptLocalInput = true)
    {
        var previousConfirmation = _confirmation;
        ImGui.TextUnformatted(Tr("Mod 在 SharpFAI 进程内运行。仅启用你信任的 Mod。", "Mods run inside SharpFAI. Enable only Mods you trust."));
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(Tr("搜索目录 ", "Search directory ") + Host.SearchDirectory);
        ImGui.PopTextWrapPos();
        if (Host.SafeMode) ImGui.TextUnformatted(Tr("安全模式已关闭所有 Mod。", "Safe mode disables all Mods."));
        ImGui.BeginDisabled((byte)(acceptLocalInput ? 0 : 1));
        ImGui.BeginDisabled((byte)(_operation is null ? 0 : 1));
        if (ImGui.Button(Tr("安装 Mod…", "Install Mod…"), default) != 0 && ChooseFile("SharpFAI Mod", ["sharpfaimod"]) is { } package)
            Start(() => Host.InstallAsync(package));
        ImGui.SameLine();
        if (ImGui.Button(Tr("添加开发版…", "Add development Mod…"), default) != 0 && ChooseFile("Mod JSON", ["json"]) is { } manifestFile)
            Start(() => Host.AddDevelopmentAsync(manifestFile));
        ImGui.SameLine();
        if (ImGui.Button(Tr("刷新", "Refresh"), default) != 0) Start(Host.RefreshAsync);
        if (_status is { } status) ImGui.TextUnformatted(status);
        ImGui.EndDisabled();
        if (_operation is not null) ImGui.TextUnformatted(Tr("正在处理 Mod…", "Updating Mods…"));

        foreach (var item in Host.Catalog)
        {
            var manifest = item.Manifest;
            ImGui.PushID(manifest.Id);
            ImGui.Separator();
            ImGui.TextUnformatted($"{manifest.Name}  {manifest.Version}");
            Muted(manifest.Id + (item.Development ? " · " + Tr("开发版", "Development") : ""));
            if (manifest.Dependencies is { Length: > 0 } dependencies)
                ImGui.TextUnformatted(Tr("依赖 ", "Dependencies ") + string.Join(", ", dependencies.Select(value => $"{value.Id} {value.Version}")));
            if (item.Unavailable is { } unavailable) ImGui.TextUnformatted(unavailable);
            if (item.Enabled) ImGui.TextUnformatted(item.Requested ? Tr("已启用", "Enabled") : Tr("由依赖链启用", "Enabled as a dependency"));
            ImGui.BeginDisabled((byte)(_operation is null ? 0 : 1));
            if (item.Enabled)
            {
                if (ImGui.Button(Tr("停用", "Disable"), default) != 0)
                    Confirm(Tr("停用选定 Mod 及依赖它的 Mod", "Disable this Mod and its dependents"), () => Host.DisableAsync(manifest.Id));
                ImGui.SameLine();
                if (ImGui.Button(Tr("热重载", "Reload"), default) != 0)
                    Confirm(Tr("信任当前文件并重载选定 Mod 及依赖它的 Mod", "Trust the current files and reload this Mod and its dependents"), () => Host.ReloadAsync(manifest.Id));
                if (!item.Requested)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(Tr("保留启用", "Keep enabled"), default) != 0)
                        Start(() => Host.EnableAsync(manifest.Id));
                }
            }
            else
            {
                ImGui.BeginDisabled((byte)(item.Unavailable is null ? 0 : 1));
                if (ImGui.Button(Tr("启用", "Enable"), default) != 0)
                    Confirm(Tr("信任并启用选定 Mod 及所需依赖", "Trust and enable this Mod and its dependencies"), () => Host.EnableAsync(manifest.Id));
                ImGui.EndDisabled();
            }
            if (item.CanRemove)
            {
                ImGui.SameLine();
                if (ImGui.Button(Tr("移除", "Remove"), default) != 0)
                    Confirm(Tr("移除选定 Mod，并停用依赖它的 Mod", "Remove this Mod and disable its dependents"), () => Host.RemoveAsync(manifest.Id));
            }
            ImGui.EndDisabled();
            if (item.Enabled)
                DrawModConfiguration(manifest.Id);
            ImGui.PopID();
        }
        DrawShortcutSettings(acceptLocalInput);
        var notices = Host.Notices;
        if (notices.Count != 0 && ImGui.CollapsingHeader(Tr("Mod 日志", "Mod log"), 0) != 0)
            DrawLog(notices);

        const string popup = "###mod-confirm";
        // The popup ID must be resolved outside the per-Mod PushID scope on both calls.
        if (_confirmation != previousConfirmation)
            ImGui.OpenPopup(popup);
        if (ImGui.BeginPopupModal(popup, 0) != 0)
        {
            if (_confirmation is { } confirmation)
            {
                ImGui.TextUnformatted(confirmation.Title);
                if (ImGui.Button(Tr("确认", "Confirm"), default) != 0 && acceptLocalInput)
                {
                    Start(confirmation.Run);
                    _confirmation = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button(Tr("取消", "Cancel"), default) != 0 && acceptLocalInput)
                {
                    _confirmation = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
        else if (ImGui.IsPopupOpen(popup) == 0)
            _confirmation = null;
        ImGui.EndDisabled();
    }

    private void DrawModConfiguration(string id)
    {
        var settings = Host.Entries<RegisteredSettings>(Host.Target)
            .FirstOrDefault(entry => entry.ModId == id && entry.IsRegistered);
        if (settings is null || ImGui.CollapsingHeader(Tr("设置###mod-config", "Settings###mod-config"), 0) == 0)
            return;
        using var lease = settings.Acquire();
        if (lease is null) return;
        ImGui.BeginDisabled((byte)(_operation is null ? 0 : 1));
        try { settings.Value.Draw(); }
        catch (Exception error) { settings.Report(error); }
        finally { ImGui.EndDisabled(); }
    }

    private unsafe void DrawLog(IReadOnlyList<ModNotice> notices)
    {
        if (!_displayedNotices.SequenceEqual(notices))
        {
            string text = string.Join('\n', notices.Reverse().Select(notice => $"[{notice.ModId}] {notice.Message}"));
            _logText = Encoding.UTF8.GetBytes(text + '\0');
            _displayedNotices = notices;
        }

        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var size = new ImVec2 { x = -1, y = Math.Clamp(ImGui.GetContentRegionAvail().y, lineHeight * 4, lineHeight * 10) };
        const ImGuiInputTextFlags flags = ImGuiInputTextFlags.ImGuiInputTextFlags_ReadOnly |
            ImGuiInputTextFlags.ImGuiInputTextFlags_WordWrap;
        fixed (byte* buffer = _logText)
            ImGui.InputTextMultiline("##mod-log", (sbyte*)buffer, (nuint)_logText.Length, size, (int)flags);
    }

    private void DrawShortcutSettings(bool acceptLocalInput)
    {
        var entries = Host.Entries<RegisteredCommand>(Host.Target);
        if (entries.Count == 0 || ImGui.CollapsingHeader(Tr("Mod 快捷键", "Mod shortcuts"), 0) == 0) return;
        if (_recordShortcut is { } recording && acceptLocalInput)
        {
            ImGui.TextUnformatted(Tr("按下新快捷键，Esc 取消。", "Press a new shortcut or Escape to cancel."));
            if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0)
                _recordShortcut = null;
            else if (TryPressedChord(out var chord))
            {
                if (entries.Any(entry => entry.Descriptor.Id != recording &&
                    string.Equals(Shortcut(entry.Value.Descriptor), chord, StringComparison.OrdinalIgnoreCase)))
                    _status = Tr("该快捷键已被另一个 Mod 使用。", "Another Mod already uses that shortcut.");
                else
                {
                    _shortcuts[recording] = chord;
                    SaveJson(_shortcutFile, _shortcuts);
                    _recordShortcut = null;
                }
            }
        }
        foreach (var entry in entries)
        {
            using var lease = entry.Acquire();
            if (lease is null) continue;
            string id = entry.Descriptor.Id;
            var descriptor = entry.Value.Descriptor;
            ImGui.PushID(id);
            ImGui.TextUnformatted(entry.Text(descriptor.TitleKey) + "  " + (Shortcut(descriptor) ?? "—"));
            if (ImGui.SmallButton(Tr("重绑", "Rebind")) != 0 && acceptLocalInput) _recordShortcut = id;
            if (_shortcuts.ContainsKey(id))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(Tr("恢复", "Reset")) != 0 && acceptLocalInput)
                {
                    _shortcuts.Remove(id);
                    SaveJson(_shortcutFile, _shortcuts);
                }
            }
            ImGui.PopID();
        }
    }

    public unsafe void DrawOverlay(ImDrawList* draw, ImVec2 start, ImVec2 end, Engine.LevelRenderer renderer, bool scene3D)
    {
        var entries = Host.Entries<Func<RenderFrame, IReadOnlyList<OverlayText>>>(Host.Target);
        if (entries.Count == 0 || renderer.Level is null) return;
        var frame = new RenderFrame(renderer.PlayTime, (int)renderer.Camera.ViewportWidth,
            (int)renderer.Camera.ViewportHeight, scene3D, Host.Target);
        ImDrawList.PushClipRect(draw, start, end, 1);
        try
        {
            foreach (var entry in entries)
            {
                using var lease = entry.Acquire();
                if (lease is null) continue;
                try
                {
                    foreach (var label in entry.Value(frame))
                    {
                        float x = label.NormalizedPosition.X, y = label.NormalizedPosition.Y;
                        if (!float.IsFinite(x) || !float.IsFinite(y)) continue;
                        ImDrawList.AddText(draw, Vec(start.x + (end.x - start.x) * x, start.y + (end.y - start.y) * y),
                            label.Color, label.Text);
                    }
                }
                catch (Exception error) { entry.Report(error); }
            }
        }
        finally { ImDrawList.PopClipRect(draw); }
    }

    private void Confirm(string title, Func<Task> operation) => _confirmation = (title, operation);

    private void Start(Func<Task> run)
    {
        if (_operation is not null) return;
        _status = null;
        _operation = Task.Run(run);
    }

    private static string? ChooseFile(string description, string[] extensions)
    {
        var dialog = NativeFileDialog.NativeFileDialog.GetFileDialog();
        return dialog?.OpenFile(description, "", new OpenFileFilter
        {
            Filter = new() { [description] = [.. extensions] },
            IncludeAllFiles = false,
        })?.FirstOrDefault();
    }

    private string Tr(string zh, string en) => Host.Culture().TwoLetterISOLanguageName == "zh" ? zh : en;
    private static string Label(string title, string id) => title + "###" + id;
    private static ImVec2 Vec(float x, float y) => new() { x = x, y = y };

    private static unsafe void Muted(string text)
    {
        var color = ImGui.GetStyleColorVec4((int)ImGuiCol.ImGuiCol_TextDisabled);
        ImGui.PushStyleColor((int)ImGuiCol.ImGuiCol_Text, *color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }
}
