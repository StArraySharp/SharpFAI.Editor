using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharpFAI.ModApi;

namespace SharpFAI.WebMirror;

internal sealed record MirrorInput(string? Type, double X, double Y, double Dx, double Dy, int Button,
    bool Down, string? Code, string? Text, bool? Ctrl = null, bool? Shift = null)
{
    public EditorWindowInput? ToEditorInput()
    {
        EditorWindowInputKind? kind = Type switch
        {
            "move" => EditorWindowInputKind.Move,
            "button" => EditorWindowInputKind.Button,
            "wheel" => EditorWindowInputKind.Wheel,
            "key" => EditorWindowInputKind.Key,
            "text" => EditorWindowInputKind.Text,
            "reset" => EditorWindowInputKind.Reset,
            _ => null,
        };
        return kind is { } value ? new(value, X, Y, Dx, Dy, Button, Down, Code, Text, Ctrl, Shift) : null;
    }
}

internal sealed class WebMirrorServer : IAsyncDisposable
{
    private const string ProtocolPrefix = "sharpfai.";
    private const int MaxMessageBytes = 8192;
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };
    private readonly WebApplication _app;
    private readonly string _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private readonly IEditorWindowStream _stream;
    private readonly IModContext _context;
    private readonly Func<int> _framesPerSecond;
    private readonly CancellationToken _lifetime;

    private WebMirrorServer(IEditorWindowStream stream, IModContext context, Func<int> framesPerSecond, CancellationToken lifetime)
    {
        _stream = stream;
        _context = context;
        _framesPerSecond = framesPerSecond;
        _lifetime = lifetime;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = Environments.Production });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentUpgradedConnections = 4;
            options.AddServerHeader = false;
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapGet("/", ServePage);
        _app.MapGet("/mirror", HandleSocket);
    }

    public string Url => $"{_app.Urls.Single()}/#{_token}";

    public static async ValueTask<WebMirrorServer> StartAsync(IEditorWindowStream stream, IModContext context,
        Func<int> framesPerSecond, CancellationToken lifetime, CancellationToken token)
    {
        var mirror = new WebMirrorServer(stream, context, framesPerSecond, lifetime);
        try
        {
            await mirror._app.StartAsync(token).ConfigureAwait(false);
            return mirror;
        }
        catch
        {
            await mirror.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsLocalRequest(HttpContext context) =>
        IPAddress.Loopback.Equals(context.Connection.RemoteIpAddress) &&
        context.Request.Host.Host == "127.0.0.1" && context.Request.Host.Port == context.Connection.LocalPort;

    private static IResult ServePage(HttpContext context)
    {
        if (!IsLocalRequest(context)) return TypedResults.StatusCode(403);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline'; " +
            "style-src 'unsafe-inline'; connect-src 'self' ws://127.0.0.1:*; img-src 'self' blob:; " +
            "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        return TypedResults.Stream(typeof(WebMirrorServer).Assembly.GetManifestResourceStream("SharpFAI.WebMirror.WebMirror.html")!,
            "text/html; charset=utf-8");
    }

    private async Task HandleSocket(HttpContext context)
    {
        if (!IsLocalRequest(context) || context.Request.Headers.Origin.Count != 1 ||
            context.Request.Headers.Origin[0] != $"http://{context.Request.Host}")
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        string protocol = ProtocolPrefix + _token;
        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(protocol, StringComparer.Ordinal))
        {
            context.Response.StatusCode = 403;
            return;
        }

        using var session = _stream.TryOpen();
        if (session is null)
        {
            context.Response.StatusCode = 409;
            return;
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, _app.Lifetime.ApplicationStopping, _lifetime);
            Task send = SendFrames(socket, session, stop.Token);
            Task receive = ReceiveInput(socket, session, stop.Token);
            await Task.WhenAny(send, receive);
            await stop.CancelAsync();
            await Task.WhenAll(send, receive);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException)
        {
        }
        catch (Exception error)
        {
            _context.Log($"Web mirror connection closed: {error.Message}");
        }
    }

    private async Task SendFrames(WebSocket socket, IEditorWindowStreamSession session, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long start = Stopwatch.GetTimestamp();
            var frame = await session.CaptureAsync(token, EditorWindowFrameFormat.Jpeg).ConfigureAwait(false);
            await socket.SendAsync(frame.Data.AsMemory(), WebSocketMessageType.Binary, true, token)
                .ConfigureAwait(false);
            TimeSpan remaining = TimeSpan.FromSeconds(1d / _framesPerSecond()) - Stopwatch.GetElapsedTime(start);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveInput(WebSocket socket, IEditorWindowStreamSession session, CancellationToken token)
    {
        byte[] buffer = new byte[MaxMessageBytes];
        while (!token.IsCancellationRequested)
        {
            int count = 0;
            ValueWebSocketReceiveResult message;
            do
            {
                if (count == buffer.Length) return;
                message = await socket.ReceiveAsync(buffer.AsMemory(count), token);
                if (message.MessageType != WebSocketMessageType.Text) return;
                count += message.Count;
            }
            while (!message.EndOfMessage);

            MirrorInput? input;
            try { input = JsonSerializer.Deserialize<MirrorInput>(buffer.AsSpan(0, count), InputJson); }
            catch (JsonException) { return; }
            if (input?.ToEditorInput() is not { } editorInput) return;
            try { await session.SendInputAsync(editorInput, token); }
            catch (ArgumentException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync().ConfigureAwait(false); }
        finally { await _app.DisposeAsync().ConfigureAwait(false); }
    }
}
