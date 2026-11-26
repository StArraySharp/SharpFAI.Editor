using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using SharpFAI.ModApi;

namespace SharpFAI.Mcp;

public sealed class McpMod : ISharpFaiMod, IModActivationObserver, IModSettings
{
    private const int DefaultPort = 37181;
    private sealed record LegacySettings(int Port = DefaultPort, bool AutoStart = true);
    private static readonly JsonSerializerOptions LegacyJson = new(JsonSerializerDefaults.Web);
    private static readonly ToolWindowId Window = new("com.sharpfai.mcp/status");
    private readonly SemaphoreSlim _listenerLock = new(1);
    private IModContext _context = null!;
    private IToolWindowRegistry _windows = null!;
    private IModSettingsStore _store = null!;
    private EditorTools _tools = null!;
    private WebApplication? _app;
    private CancellationToken _lifetime;
    private volatile string? _boundAddress;
    private volatile string? _error;

    [ModSettingLabel("Local Port"), ModSettingRange(1024, 65535), ModSettingTooltip("Port Change Help")]
    private int Port { get; set; } = DefaultPort;

    [ModSettingLabel("Start Automatically")]
    private bool AutoStart { get; set; } = true;

    [ModSettingHidden]
    private string? Token { get; set; }

    private static string Authority(int port) => "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

    public ValueTask ActivateAsync(IModContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _lifetime = cancellationToken;
        _windows = context.GetRequiredService<IToolWindowRegistry>();
        _store = context.GetRequiredService<IModSettingsStore>();
        _tools = new(context.GetRequiredService<IEditorInspection>(), context.GetRequiredService<IEditorFrameCapture>(),
            context.GetRequiredService<IChartEdits>(), context.GetRequiredService<IPlaybackControl>());
        _windows.Register(new(Window, "mcp-window", UiHost.Editor, ToolWindowKind.Floating, ToolWindowScope.Application,
            ShowInWindowMenu: false, MinimumSize: new(500, 160)), () => new StatusWindow(this));
        return ValueTask.CompletedTask;
    }

    public async ValueTask OnActivatedAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureConnectionSettings();
            if (AutoStart) await SetRunningAsync(true, remember: false, cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            ReportError(error);
        }
    }

    [MenuItem("Tools/MCP", SnapshotChart = false)]
    private async ValueTask OpenStatus(CancellationToken token) => await _windows.OpenAsync(Window, token);

    private async ValueTask SetRunningAsync(bool run, bool remember, CancellationToken token)
    {
        await _listenerLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _error = null;
            if (run && _app is null)
            {
                string tokenValue = EnsureConnectionSettings();
                var app = CreateListener(Port, tokenValue);
                try { await app.StartAsync(token).ConfigureAwait(false); }
                catch
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                _app = app;
                _boundAddress = app.Urls.Single();
            }
            else if (!run && _app is not null)
            {
                await StopAsync().ConfigureAwait(false);
            }
            if (remember && AutoStart != run)
            {
                bool previous = AutoStart;
                AutoStart = run;
                try { _store.Save(); }
                catch { AutoStart = previous; throw; }
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            ReportError(error);
        }
        finally { _listenerLock.Release(); }
    }

    private string EnsureConnectionSettings()
    {
        bool initialize = !File.Exists(_store.FilePath);
        if (initialize) ReadLegacySettings();
        string? previous = Token;
        string token = string.IsNullOrWhiteSpace(previous)
            ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) : previous;
        if (!IsToken(token))
            throw new InvalidDataException("MCP Token must contain 64 hexadecimal characters.");
        if (initialize || previous != token)
        {
            Token = token;
            try { _store.Save(); }
            catch { Token = previous; throw; }
        }
        return token;
    }

    private void ReadLegacySettings()
    {
        try
        {
            string directory = _context.DataDirectory;
            string settings = Path.Combine(directory, "settings.json");
            if (File.Exists(settings) && JsonSerializer.Deserialize<LegacySettings>(File.ReadAllText(settings), LegacyJson) is { } legacy)
            {
                Port = Math.Clamp(legacy.Port, 1024, 65535);
                AutoStart = legacy.AutoStart;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            _context.Log($"Could not import the old MCP settings: {error.Message}");
        }
        try
        {
            string tokenFile = Path.Combine(_context.DataDirectory, "token.txt");
            if (!File.Exists(tokenFile)) return;
            string token = File.ReadAllText(tokenFile).Trim();
            if (IsToken(token)) Token = token;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _context.Log($"Could not import the old MCP token: {error.Message}");
        }
    }

    private static bool IsToken(string token) => token.Length == 64 && token.All(Uri.IsHexDigit);

    private WebApplication CreateListener(int port, string token)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 512 * 1024;
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "SharpFAI", Version = _context.Version })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(_tools, EditorTools.Json);
        var app = builder.Build();
        string authority = Authority(port);
        byte[] expected = Encoding.ASCII.GetBytes("Bearer " + token);
        app.Use(async (http, next) =>
        {
            if (!string.Equals(http.Request.Host.Value, authority, StringComparison.Ordinal) ||
                http.Request.Headers.TryGetValue(HeaderNames.Origin, out var origin) &&
                (origin.Count != 1 || origin[0] != $"http://{authority}"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var authorization = http.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { } value || value.Length != expected.Length ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), expected))
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
                http.RequestAborted, _lifetime, app.Lifetime.ApplicationStopping);
            http.RequestAborted = stopping.Token;
            await next(http).ConfigureAwait(false);
        });
        app.MapMcp("/mcp");
        return app;
    }

    private void ReportError(Exception error)
    {
        _error = error.GetBaseException().Message;
        _context.Log(_error);
        Console.Error.WriteLine($"SharpFAI MCP: {_error}");
    }

    private async ValueTask StopAsync()
    {
        var app = _app;
        _app = null;
        _boundAddress = null;
        if (app is null) return;
        try { await app.StopAsync().ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        await _listenerLock.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _listenerLock.Dispose(); }
    }

    private sealed class StatusWindow(McpMod mod) : IToolWindow
    {
        public IReadOnlyList<UiNode> Build(in ToolWindowContext context)
        {
            string? address = mod._boundAddress;
            return
            [
                new UiText(address is not null ? "MCP 已运行 / Running" : "MCP 已停止 / Stopped"),
                new UiText($"{address ?? $"http://{Authority(mod.Port)}"}/mcp"),
                new UiText($"设置及 Token / Settings and Token\n{mod._store.FilePath}", Muted: true),
                new UiButton("start", "启动 / Start", address is null),
                new UiButton("stop", "停止 / Stop", address is not null),
                new UiText(mod._error ?? string.Empty),
            ];
        }

        public ValueTask HandleAsync(UiAction action, CancellationToken token) =>
            mod.SetRunningAsync(action.Id == "start", remember: true, token);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
