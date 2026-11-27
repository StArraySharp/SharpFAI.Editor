using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SharpFAI.Editor.Mods;
using SharpFAI.ModApi;

namespace SharpFAI.Editor.Tests;

public sealed class McpModTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ListenerUsesSharedSettingsAndReusesItsAddressOnReload(bool migrate)
    {
        using var root = Directory.CreateTempSubdirectory("sharpfai-mcp-").AsScope();
        string archive = Path.Combine(AppContext.BaseDirectory, "ModFixtures", "SharpFAI.Mcp.sharpfaimod");
        const string id = "com.sharpfai.mcp";
        var services = new Services();
        await using var host = new ModHost(UiHost.Editor, root.Path)
        {
            ExternalService = type => type.IsInstanceOfType(services) ? services : null,
        };
        await host.InstallAsync(archive);
        string settingsPath = Path.Combine(root.Path, "SharpFAI.Mcp.Settings.json");
        var candidate = new TcpListener(IPAddress.Loopback, 0);
        candidate.Start();
        int port = ((IPEndPoint)candidate.LocalEndpoint).Port;
        candidate.Stop();
        const string legacyToken = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        if (migrate)
        {
            string data = host.GetDataDirectory(id);
            File.WriteAllText(Path.Combine(data, "settings.json"), JsonSerializer.Serialize(new { port }));
            File.WriteAllText(Path.Combine(data, "token.txt"), legacyToken);
        }
        else
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { Port = port, AutoStart = true }));

        using var client = new HttpClient
        {
            BaseAddress = new($"http://127.0.0.1:{port}/mcp"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        await host.EnableAsync(id);
        Assert.That(host.Notices.Select(notice => notice.Message), Is.Empty);
        using (var unauthorized = await Send("ping", new { }))
            Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        string token = ReadToken();
        Assert.That(token, Has.Length.EqualTo(64));
        if (OperatingSystem.IsWindows())
            Assert.That(new FileInfo(settingsPath).GetAccessControl().AreAccessRulesProtected, Is.True);
        else
            Assert.That(File.GetUnixFileMode(settingsPath), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        if (migrate) Assert.That(token, Is.EqualTo(legacyToken));
        else Assert.That(File.Exists(Path.Combine(root.Path, "data", id, "token.txt")), Is.False);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var foreignOrigin = await Send("ping", new { }, "http://example.invalid"))
            Assert.That(foreignOrigin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        await InitializeAndFindTools();

        await host.ReloadAsync(id);
        Assert.That(host.Notices.Select(notice => notice.Message), Is.Empty);
        Assert.That(ReadToken(), Is.EqualTo(token));
        await InitializeAndFindTools();
        await host.DisableAsync(id);
        Assert.ThrowsAsync<HttpRequestException>(async () => { using var _ = await Send("ping", new { }); });

        string ReadToken()
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.That(settings.RootElement.GetProperty("Port").GetInt32(), Is.EqualTo(port));
            return settings.RootElement.GetProperty("Token").GetString()!;
        }

        async Task InitializeAndFindTools()
        {
            client.DefaultRequestHeaders.Remove("MCP-Protocol-Version");
            using var init = await Send("initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "SharpFAI test", version = "1.0" },
            });
            var protocol = (await Result(init)).GetProperty("protocolVersion").GetString();
            client.DefaultRequestHeaders.Add("MCP-Protocol-Version", protocol);
            using var response = await Send("tools/list", new { });
            var tools = (await Result(response)).GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString());
            Assert.That(tools, Is.SupersetOf(new[]
            {
                "chart_edit", "chart_capture_frame", "chart_edit_preview", "chart_open", "chart_save",
                "chart_tempo", "chart_convert_tempo", "chart_selection", "chart_profile_playback",
                "chart_compare_frames", "chart_export_video", "chart_video_status",
            }));
        }

        async Task<HttpResponseMessage> Send(string method, object parameters, string? origin = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }),
                    Encoding.UTF8, "application/json"),
            };
            if (origin is not null) request.Headers.Add("Origin", origin);
            return await client.SendAsync(request);
        }

        static async Task<JsonElement> Result(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync();
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                body = body.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("result").Clone();
        }
    }

    // This fixture checks transport and registration. The editor provides these services in actual tool calls.
    private sealed class Services : IEditorInspection, IEditorFrameCapture, IChartEdits, IPlaybackControl
    {
        public ValueTask<ChartContext> GetChartAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ChartPosition> LocateAsync(ChartRevision expected, int? floor, double? seconds, double? quarterNote,
            CancellationToken token) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<ChartFloor>> ReadFloorsAsync(ChartRevision expected, int firstFloor, int count,
            CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ChartEventPage> ReadEventsAsync(ChartRevision expected, ChartEventCursor start, int lastFloor,
            string? type, string? tag, int limit, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ChartDecorationPage> ReadDecorationsAsync(ChartRevision expected, int startIndex, string? type,
            string? tag, int limit, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ChartDiagnostics> GetDiagnosticsAsync(ChartRevision expected, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ChartEventSchema?> GetEventSchemaAsync(string type, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<ChartFrameCapture> CaptureAsync(FrameCaptureRequest request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<EditResult> CommitAsync(ChartRevision expected, ChartEditRequest edit, CancellationToken token) =>
            throw new NotSupportedException();
        public ValueTask<bool> RequestAsync(TransportAction action, double time, CancellationToken token) => throw new NotSupportedException();
    }
}
