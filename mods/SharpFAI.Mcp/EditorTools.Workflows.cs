using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SharpFAI.ModApi;

namespace SharpFAI.Mcp;

internal sealed partial class EditorTools
{
    [McpServerTool(Name = "chart_edit_preview", ReadOnly = true, OpenWorld = false)]
    [Description("Dry-run the same ordered operations as chart_edit. Returns before/after values and the first rejected operation. Does not touch the open document.")]
    public async Task<ChartEditPreview> PreviewEdit(ChartRevision revision, string description, EditOperation[] operations,
        CancellationToken token)
    {
        try
        {
            if (operations is { Length: > 0 and <= 256 })
                for (int i = 0; i < operations.Length; i++)
                    if (operations[i]?.ToChartOperation() is null)
                        return new(revision, false, i, "Invalid or incomplete operation.", []);
            var edit = EditRequest(description, operations);
            return await Read(() => Workflows.PreviewEditAsync(revision, edit, token));
        }
        catch (McpException error) { return new(revision, false, null, error.Message, []); }
    }

    [McpServerTool(Name = "chart_open", Destructive = true, OpenWorld = false)]
    [Description("Open an absolute local .adofai or .json path. Unsaved edits are protected unless discardUnsaved is true. Pass revision to pin the current document before opening another.")]
    public async Task<ChartFileResult> Open(string path, CancellationToken token, ChartRevision? revision = null,
        bool discardUnsaved = false)
    {
        var expected = revision ?? (await chart.GetChartAsync(token)).Revision;
        return await Read(() => Workflows.OpenAsync(expected, path, discardUnsaved, token));
    }

    [McpServerTool(Name = "chart_save", Destructive = true, OpenWorld = false)]
    [Description("Save to an absolute local .adofai or .json path. Saving to a different existing file requires overwrite: true. Pass revision to pin the document being saved.")]
    public async Task<ChartFileResult> Save(string path, CancellationToken token, ChartRevision? revision = null,
        bool overwrite = false)
    {
        var expected = revision ?? (await chart.GetChartAsync(token)).Revision;
        return await Read(() => Workflows.SaveAsync(expected, path, overwrite, token));
    }

    [McpServerTool(Name = "chart_tempo", ReadOnly = true, OpenWorld = false)]
    [Description("Return the authored BPM, effective BPM and chart pitch at a floor.")]
    public Task<ChartTempo> Tempo(ChartRevision revision, int floor, CancellationToken token) =>
        Read(() => Workflows.ReadTempoAsync(revision, floor, token));

    [McpServerTool(Name = "chart_convert_tempo", ReadOnly = true, OpenWorld = false)]
    [Description("Calculate the BPM or multiplier for an existing SetSpeed event to reach a target effective BPM. eventIndex is the zero-based index within that floor. Does not modify the chart.")]
    public Task<ChartSpeedConversion> ConvertTempo(ChartRevision revision, int floor, int eventIndex, double targetBpm,
        CancellationToken token) => Read(() => Workflows.ConvertTempoAsync(revision, floor, eventIndex, targetBpm, token));

    [McpServerTool(Name = "chart_selection", Destructive = false, OpenWorld = false)]
    [Description("Read, set, clear, loop or stopLoop the editor's A–B range. Set takes start and end in music-file seconds.")]
    public Task<ChartSelection> Selection(ChartRevision revision, ChartSelectionAction action, CancellationToken token,
        double? start = null, double? end = null)
    {
        if (start.HasValue != end.HasValue) throw new McpException("Provide both start and end.");
        return Read(() => Workflows.SelectAsync(revision, action, start is double a && end is double b ? new(a, b) : null, token));
    }

    [McpServerTool(Name = "chart_profile_playback", Destructive = false, OpenWorld = false)]
    [Description("Play an interval of up to 60 music-file seconds in the live editor window. Returns mean, P95/P99, max frame interval and slow frames with their time, floor and measured CPU stages.")]
    public Task<ChartPerformanceReport> Profile(ChartRevision revision, double start, double end, CancellationToken token,
        int slowest = 10) => Read(() => Workflows.ProfileAsync(revision, new(start, end), slowest, token));

    [McpServerTool(Name = "chart_compare_frames", ReadOnly = true, OpenWorld = false)]
    [Description("Compare an offscreen capture with an absolute reference PNG of the same dimensions or a proposed chart edit. Uses same time, resolution and preroll and returns an RGB difference image and metadata.")]
    public async Task<CallToolResult> Compare(ChartRevision revision, CancellationToken token, string? referencePng = null,
        EditOperation[]? operations = null, int? floor = null, double? seconds = null,
        int width = 1280, int height = 720, double prerollSeconds = 0, int prerollFps = 60)
    {
        var edit = operations is null ? null : EditRequest("Frame comparison", operations);
        var diff = await Read(() => Workflows.CompareAsync(
            new(revision, floor, seconds, width, height, prerollSeconds, prerollFps), edit, referencePng, token));
        var metadata = JsonSerializer.SerializeToElement(new
        {
            revision, diff.Current, diff.Reference, diff.Width, diff.Height, diff.ChangedPixels,
            diff.MaxChannelDifference, diff.MeanChannelDifference, diff.CameraChanged, diff.FiltersChanged,
        }, Json);
        return new()
        {
            StructuredContent = metadata,
            Content = [new TextContentBlock { Text = metadata.GetRawText() }, ImageContentBlock.FromBytes(diff.DifferencePng, "image/png")],
        };
    }

    [McpServerTool(Name = "chart_export_video", Destructive = true, OpenWorld = false)]
    [Description("Start MP4 export of the current A–B selection to an absolute path. Use chart_video_status with the returned job id to poll progress.")]
    public Task<ChartVideoJob> ExportVideo(ChartRevision revision, string outputPath, CancellationToken token,
        int width = 1920, int height = 1080, int fps = 60, double prerollSeconds = 2,
        bool preferHardware = true, bool overwrite = false) =>
        Read(() => Workflows.ExportAsync(revision, new(outputPath, width, height, fps, prerollSeconds, preferHardware, overwrite), token));

    [McpServerTool(Name = "chart_video_status", Destructive = false, OpenWorld = false)]
    [Description("Read an MCP video export's status. Set cancel to true to request cancellation.")]
    public Task<ChartVideoJob> VideoStatus(Guid job, CancellationToken token, bool cancel = false) =>
        Read(() => Workflows.VideoStatusAsync(job, cancel, token));
}
