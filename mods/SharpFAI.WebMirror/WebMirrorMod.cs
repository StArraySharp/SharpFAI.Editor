using System.Diagnostics;
using SharpFAI.ModApi;

namespace SharpFAI.WebMirror;

public sealed class WebMirrorMod : ISharpFaiMod, IModActivationObserver, IModSettings
{
    private static readonly ToolWindowId Window = new("com.sharpfai.web-mirror/status");
    private readonly SemaphoreSlim _listenerLock = new(1);
    private IModContext _context = null!;
    private IToolWindowRegistry _windows = null!;
    private IEditorWindowStream _stream = null!;
    private CancellationToken _lifetime;
    private WebMirrorServer? _server;
    private volatile string? _url;
    private volatile string? _error;
    private int _framesPerSecond = 24;

    [ModSettingLabel("Start Automatically")]
    private bool AutoStart { get; set; } = true;

    [ModSettingLabel("Maximum Frame Rate"), ModSettingRange(1, 60)]
    private int FramesPerSecond
    {
        get => Volatile.Read(ref _framesPerSecond);
        set => Volatile.Write(ref _framesPerSecond, value);
    }

    public ValueTask ActivateAsync(IModContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _lifetime = cancellationToken;
        _stream = context.GetRequiredService<IEditorWindowStream>();
        _windows = context.GetRequiredService<IToolWindowRegistry>();
        _windows.Register(new(Window, "Web Mirror", UiHost.Editor, ToolWindowKind.Floating, ToolWindowScope.Application,
            ShowInWindowMenu: false, MinimumSize: new(480, 145)), () => new StatusWindow(this));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnActivatedAsync(CancellationToken cancellationToken) =>
        AutoStart ? SetRunningAsync(true, cancellationToken) : ValueTask.CompletedTask;

    [MenuItem("Tools/Web Mirror", SnapshotChart = false)]
    private async ValueTask OpenStatus(CancellationToken token) => await _windows.OpenAsync(Window, token);

    private async ValueTask SetRunningAsync(bool running, CancellationToken token)
    {
        await _listenerLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _error = null;
            if (running && _server is null)
            {
                var server = await WebMirrorServer.StartAsync(_stream, _context, () => FramesPerSecond, _lifetime, token)
                    .ConfigureAwait(false);
                _server = server;
                _url = server.Url;
                Console.WriteLine($"SharpFAI Web Mirror: {_url}");
            }
            else if (!running) await StopAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            ReportError(error);
        }
        finally { _listenerLock.Release(); }
    }

    private async ValueTask StopAsync()
    {
        var server = _server;
        _server = null;
        _url = null;
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
    }

    private void OpenBrowser()
    {
        if (_url is not { } url) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error) { ReportError(error); }
    }

    private void ReportError(Exception error)
    {
        _error = error.GetBaseException().Message;
        _context.Log(_error);
    }

    public async ValueTask DisposeAsync()
    {
        await _listenerLock.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _listenerLock.Dispose(); }
    }

    private sealed class StatusWindow(WebMirrorMod mod) : IToolWindow
    {
        public IReadOnlyList<UiNode> Build(in ToolWindowContext context)
        {
            string? url = mod._url;
            string Text(string key) => mod._context.Text(key);
            List<UiNode> controls =
            [
                new UiText(Text(url is null ? "Stopped" : "Running")),
                new UiButton("browser", Text("Open in Browser"), url is not null),
                new UiButton("start", Text("Start"), url is null),
                new UiButton("stop", Text("Stop"), url is not null),
            ];
            if (url is not null) controls.Add(new UiText(url, Muted: true));
            if (mod._error is { } error) controls.Add(new UiText(error));
            return controls;
        }

        public ValueTask HandleAsync(UiAction action, CancellationToken token)
        {
            switch (action.Id)
            {
                case "browser": mod.OpenBrowser(); break;
                case "start": return mod.SetRunningAsync(true, token);
                case "stop": return mod.SetRunningAsync(false, token);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
