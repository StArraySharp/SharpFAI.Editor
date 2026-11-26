using System.Net;
using System.Net.WebSockets;
using System.Text;
using NUnit.Framework;
using SharpFAI.Editor.Mods;
using SharpFAI.ModApi;
using SharpFAI.Player.ModSupport;

namespace SharpFAI.Editor.Tests;

public sealed class WebMirrorModTests
{
    [Test]
    public async Task ReloadDisconnectsMirrorAndReleasesWindowStream()
    {
        const string id = "com.sharpfai.web-mirror";
        using var root = Directory.CreateTempSubdirectory("sharpfai-web-mirror-").AsScope();
        using var stream = new EditorWindowStream();
        await using var host = new ModHost(UiHost.Editor, root.Path)
        {
            ExternalService = type => type == typeof(IEditorWindowStream) ? stream : null,
        };
        await host.InstallAsync(Path.Combine(AppContext.BaseDirectory, "ModFixtures", "SharpFAI.WebMirror.sharpfaimod"));
        await host.EnableAsync(id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new HttpClient();
        Uri link = GetLink();
        Assert.That(await client.GetStringAsync(link, timeout.Token), Does.Contain("mirror-canvas"));
        Assert.That(stream.Current, Is.Null);

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("sharpfai." + link.Fragment[1..]);
        socket.Options.SetRequestHeader("Origin", link.GetLeftPart(UriPartial.Authority));
        await socket.ConnectAsync(new UriBuilder(link) { Scheme = "ws", Path = "/mirror", Fragment = "" }.Uri, timeout.Token);
        Assert.That(stream.TryOpen(), Is.Null);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"text","text":"节奏"}""").AsMemory(),
            WebSocketMessageType.Text, true, timeout.Token);
        EditorWindowInput? input;
        while (!stream.Current!.TryRead(out input))
            await Task.Delay(10, timeout.Token);
        Assert.That(input, Is.EqualTo(new EditorWindowInput(EditorWindowInputKind.Text, Text: "节奏")));

        await host.ReloadAsync(id);
        Assert.That(stream.Current, Is.Null);
        Uri newLink = GetLink();
        Assert.That(newLink.Fragment, Is.Not.EqualTo(link.Fragment));
        Assert.That(await client.GetStringAsync(newLink, timeout.Token), Does.Contain("mirror-canvas"));
        await host.DisableAsync(id);
        Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetStringAsync(newLink, timeout.Token));

        Uri GetLink()
        {
            Assert.That(host.Notices.Select(notice => notice.Message), Is.Empty);
            var window = host.Entries<RegisteredWindow>(UiHost.Editor).Single().Value.Create();
            try
            {
                return new(window.Build(new(new(null, new(PlaybackMode.Edit, 0, 0, 0, false)), false))
                    .OfType<UiText>().Single(text => text.Value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)).Value);
            }
            finally { window.DisposeAsync().GetAwaiter().GetResult(); }
        }
    }

    [Test]
    public async Task WindowFrameRequestsCanBeCancelledAndDoNotSurviveDisconnect()
    {
        using var stream = new EditorWindowStream();
        using var session = stream.TryOpen()!;
        using var cancel = new CancellationTokenSource();
        var pending = session.CaptureAsync(cancel.Token).AsTask();
        await cancel.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(async () => await pending);
        var next = session.CaptureAsync(default).AsTask();
        session.Dispose();
        Assert.CatchAsync<OperationCanceledException>(async () => await next);
        using var replacement = stream.TryOpen();
        Assert.That(replacement, Is.Not.Null);
    }
}
