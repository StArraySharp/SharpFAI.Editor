using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SharpFAI.ModApi;

namespace SharpFAI.Mcp;

internal sealed class EditorTools(IEditorInspection chart, IEditorFrameCapture frames, IChartEdits edits, IPlaybackControl playback)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [McpServerTool(Name = "chart_context", ReadOnly = true, OpenWorld = false)]
    [Description("Read the open SharpFAI chart including unsaved changes, selection, settings, revision and playback state.")]
    public async Task<ChartContext> GetChart(CancellationToken token) => await chart.GetChartAsync(token);

    [McpServerTool(Name = "chart_locate", ReadOnly = true, OpenWorld = false)]
    [Description("Convert a floor (zero-based), music-file time in seconds, or quarter-note position to floor, time, BPM and measure. Provide exactly one position.")]
    public Task<ChartPosition> Locate(ChartRevision revision, CancellationToken token, int? floor = null,
        double? seconds = null, double? quarterNote = null) => Read(() => chart.LocateAsync(revision, floor, seconds, quarterNote, token));

    [McpServerTool(Name = "chart_floors", ReadOnly = true, OpenWorld = false)]
    [Description("Read up to 512 consecutive floors, their raw incoming angle (999 is a midspin), and timing when available.")]
    public Task<IReadOnlyList<ChartFloor>> Floors(ChartRevision revision, int firstFloor, CancellationToken token,
        int count = 64) => Read(() => chart.ReadFloorsAsync(revision, firstFloor, count, token));

    [McpServerTool(Name = "chart_events", ReadOnly = true, OpenWorld = false)]
    [Description("Search ordinary actions in an inclusive zero-based floor range. Optional type and tag filters match exactly. Pass next.floor and next.index as firstFloor and firstIndex to continue, even when a page is empty.")]
    public Task<ChartEventPage> Events(ChartRevision revision, int firstFloor, int lastFloor, CancellationToken token,
        int firstIndex = 0, string? type = null, string? tag = null, int limit = 64) =>
        Read(() => chart.ReadEventsAsync(revision, new(firstFloor, firstIndex), lastFloor, type, tag, limit, token));

    [McpServerTool(Name = "chart_decorations", ReadOnly = true, OpenWorld = false)]
    [Description("Read raw image, text or object decoration instances, optionally filtered by type or tag. Continue with nextIndex, even when a page is empty.")]
    public Task<ChartDecorationPage> Decorations(ChartRevision revision, CancellationToken token, int startIndex = 0,
        string? type = null, string? tag = null, int limit = 64) =>
        Read(() => chart.ReadDecorationsAsync(revision, startIndex, type, tag, limit, token));

    [McpServerTool(Name = "chart_event_schema", ReadOnly = true, OpenWorld = false)]
    [Description("Look up the editor schema for an event or decoration type, including defaults, units, ranges and options.")]
    public async Task<ChartEventSchema> Schema(string type, CancellationToken token) =>
        await chart.GetEventSchemaAsync(type, token) ?? throw new McpException($"Unknown editor event type '{type}'.");

    [McpServerTool(Name = "chart_diagnostics", ReadOnly = true, OpenWorld = false)]
    [Description("Read the editor renderer's load error and warnings, including reported asset problems. Warnings apply to the requested revision when rendererCurrent is true.")]
    public Task<ChartDiagnostics> Diagnostics(ChartRevision revision, CancellationToken token) =>
        Read(() => chart.GetDiagnosticsAsync(revision, token));

    [McpServerTool(Name = "chart_capture_frame", ReadOnly = true, OpenWorld = false)]
    [Description("Render a gameplay frame from the current chart revision without moving the editor playhead. Provide floor or music-file seconds. Returns the camera, ordered filter passes and optionally a PNG. Use prerollSeconds for effects that depend on recent frames.")]
    public async Task<CallToolResult> Capture(ChartRevision revision, CancellationToken token, int? floor = null,
        double? seconds = null, int width = 1280, int height = 720, double prerollSeconds = 0, int prerollFps = 60,
        bool includeImage = true)
    {
        var capture = await Read(() => frames.CaptureAsync(
            new(revision, floor, seconds, width, height, prerollSeconds, prerollFps, includeImage), token));
        JsonElement metadata = JsonSerializer.SerializeToElement(new
            { capture.Revision, capture.Width, capture.Height, capture.State }, Json);
        var result = new CallToolResult { StructuredContent = metadata, Content = [new TextContentBlock { Text = metadata.GetRawText() }] };
        if (capture.Png is { } png)
            result.Content.Add(ImageContentBlock.FromBytes(png, "image/png"));
        return result;
    }

    [McpServerTool(Name = "chart_edit", Destructive = true, OpenWorld = false)]
    [Description("Apply an atomic batch to the open chart in edit mode as one undo step. Requires the latest revision. It does not save the file. Operations use the indices in the document after preceding operations in this batch.")]
    public async Task<CallToolResult> Edit(ChartRevision revision, string description, EditOperation[] operations,
        CancellationToken token)
    {
        if (operations is not { Length: > 0 and <= 256 } || string.IsNullOrWhiteSpace(description))
            throw new McpException("Provide a description and 1 to 256 operations.");
        var batch = operations.Select((operation, index) => operation?.ToChartOperation() ??
            throw new McpException($"Invalid or incomplete operation at index {index}.")).ToArray();
        var result = await edits.CommitAsync(revision, new(description, batch), token);
        if (result != EditResult.Applied)
            throw new McpException(result == EditResult.RevisionConflict
                ? "Chart revision changed. Call chart_context and re-read the affected floors or decorations."
                : "The editor rejected the batch. Check the operations and confirm the editor is in edit mode.");
        var value = JsonSerializer.SerializeToElement(new { applied = true, baseRevision = revision }, Json);
        return new() { StructuredContent = value, Content = [new TextContentBlock { Text = value.GetRawText() }] };
    }

    [McpServerTool(Name = "chart_transport", Destructive = false, OpenWorld = false)]
    [Description("Play automatically, pause into preview, seek to a music-file time, or return to edit mode.")]
    public async Task<PlaybackState> Transport(TransportAction action, CancellationToken token, double? seconds = null)
    {
        if (!Enum.IsDefined(action) || action == TransportAction.Seek &&
            (seconds is null || !double.IsFinite(seconds.Value) || seconds < 0))
            throw new McpException("Provide a valid action and, for a seek, a finite nonnegative time in seconds.");
        if (!await playback.RequestAsync(action, seconds ?? 0, token))
            throw new McpException("Playback control is unavailable in the current editor mode or while the chart is loading.");
        return (await chart.GetChartAsync(token)).Playback;
    }

    private static async Task<T> Read<T>(Func<ValueTask<T>> read)
    {
        try { return await read(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            throw new McpException(error.Message);
        }
    }

    public sealed record EditOperation(
        [property: Description("insertAngles, deleteAngles, setSetting, addEvent, updateEvent, removeEvent, addDecoration, updateDecoration, or removeDecoration.")]
        string Kind, int? Floor = null,
        [property: Description("Event index within the floor, or global decoration index. Both are zero-based.")]
        int? Index = null,
        int? Count = null, double[]? Angles = null, string? Type = null, string? Key = null,
        JsonElement? Value = null, JsonElement? Fields = null)
    {
        public ChartOperation? ToChartOperation() => Kind switch
        {
            "insertAngles" when Floor is int floor && Angles is { Length: > 0 and <= 4096 } => new InsertAngles(floor, Angles),
            "deleteAngles" when Floor is int floor && Count is > 0 => new DeleteAngles(floor, Count.Value),
            "setSetting" when !string.IsNullOrWhiteSpace(Key) && Value is { } value =>
                new SetChartSetting(Key, value),
            "addEvent" when Floor is int floor && Type is { } type && Fields is { ValueKind: JsonValueKind.Object } fields =>
                new AddChartEvent(floor, type, fields),
            "updateEvent" when Floor is int floor && Index is int index && Fields is { ValueKind: JsonValueKind.Object } fields =>
                new UpdateChartEvent(floor, index, fields),
            "removeEvent" when Floor is int floor && Index is int index => new RemoveChartEvent(floor, index),
            "addDecoration" when Floor is int floor && Type is { } type && Fields is { ValueKind: JsonValueKind.Object } fields =>
                new AddChartDecoration(floor, type, fields),
            "updateDecoration" when Index is int index && Fields is { ValueKind: JsonValueKind.Object } fields =>
                new UpdateChartDecoration(index, fields),
            "removeDecoration" when Index is int index => new RemoveChartDecoration(index),
            _ => null,
        };
    }
}
