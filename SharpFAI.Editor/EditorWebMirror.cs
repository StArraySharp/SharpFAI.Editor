using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SharpFAI.Player;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace SharpFAI.Editor;

internal sealed record MirrorInput(string? Type, double X, double Y, double Dx, double Dy, int Button,
    bool Down, string? Code, string? Text, bool? Ctrl = null, bool? Shift = null);

internal sealed class EditorWebMirror : IDisposable
{
    internal sealed class Connection
    {
        public readonly Channel<MirrorInput> Input = Channel.CreateBounded<MirrorInput>(256);
        public readonly Channel<byte[]> Frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    private const string ProtocolPrefix = "sharpfai.";
    private const int MaxMessageBytes = 8192;
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };
    private readonly WebApplication _app;
    private readonly string _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private readonly CancellationTokenSource _shutdown = new();
    private Connection? _connection;
    private Task _encoding = Task.CompletedTask;
    private long _nextFrame;

    private EditorWebMirror()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentUpgradedConnections = 4;
            options.AddServerHeader = false;
        });
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapGet("/", ServePage);
        _app.MapGet("/mirror", HandleSocket);
    }

    public static EditorWebMirror Start()
    {
        var mirror = new EditorWebMirror();
        try
        {
            mirror._app.StartAsync().GetAwaiter().GetResult();
            Console.WriteLine($"SharpFAI Web UI: {mirror._app.Urls.Single()}/#{mirror._token}");
            return mirror;
        }
        catch
        {
            mirror.Dispose();
            throw;
        }
    }

    public Connection? Client => Volatile.Read(ref _connection);

    public void CaptureFrame(int width, int height)
    {
        long now = Stopwatch.GetTimestamp();
        if (Client is not { } client || !_encoding.IsCompleted || now < _nextFrame || width <= 0 || height <= 0)
            return;
        _nextFrame = now + Stopwatch.Frequency / 24;
        byte[] pixels = FrameCapture.ReadPixels(width, height);
        _encoding = Task.Run(() =>
        {
            try
            {
                using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
                using var stream = new MemoryStream();
                image.SaveAsJpeg(stream, new JpegEncoder { Quality = 85 });
                client.Frames.Writer.TryWrite(stream.ToArray());
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Web UI frame encoding failed: {error.Message}");
            }
        });
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
        return TypedResults.Stream(typeof(EditorWebMirror).Assembly.GetManifestResourceStream("SharpFAI.Editor.WebMirror.html")!,
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

        var client = new Connection();
        if (Interlocked.CompareExchange(ref _connection, client, null) is not null)
        {
            context.Response.StatusCode = 409;
            return;
        }
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _shutdown.Token);
            Task send = SendFrames(socket, client, stop.Token);
            Task receive = ReceiveInput(socket, client, stop.Token);
            await Task.WhenAny(send, receive);
            await stop.CancelAsync();
            await Task.WhenAll(send, receive);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException)
        {
        }
        finally
        {
            Volatile.Write(ref _connection, null);
            client.Frames.Writer.TryComplete();
            client.Input.Writer.TryComplete();
        }
    }

    private static async Task SendFrames(WebSocket socket, Connection client, CancellationToken token)
    {
        await foreach (byte[] frame in client.Frames.Reader.ReadAllAsync(token))
            await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, token);
    }

    private static async Task ReceiveInput(WebSocket socket, Connection client, CancellationToken token)
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
            if (!IsValid(input)) return;
            await client.Input.Writer.WriteAsync(input!, token);
        }
    }

    private static bool IsValid(MirrorInput? input) => input?.Type switch
    {
        "move" => IsPoint(input),
        "button" => IsPoint(input) && input.Button is >= 0 and <= 2,
        "wheel" => IsPoint(input) && input.Dx is >= -20 and <= 20 && input.Dy is >= -20 and <= 20,
        "key" => input.Code is { Length: > 0 and <= 32 },
        "text" => input.Text is { Length: > 0 and <= 512 },
        "reset" => true,
        _ => false
    };

    private static bool IsPoint(MirrorInput input) => input.X is >= 0 and <= 1 && input.Y is >= 0 and <= 1;

    public void Dispose()
    {
        _shutdown.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { _app.StopAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _encoding.GetAwaiter().GetResult();
        _shutdown.Dispose();
    }
}
