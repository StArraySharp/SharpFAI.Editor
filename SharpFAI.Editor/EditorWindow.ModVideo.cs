using SharpFAI.ModApi;
using SharpFAI.Player;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private ChartVideoJob? _modVideoJob;

    private sealed partial class ModEditorInspection
    {
        public ValueTask<ChartVideoJob> ExportAsync(ChartRevision revision, ChartVideoOptions options, CancellationToken token) =>
            ui.Dispatch(() =>
            {
                var document = CheckRevision(revision);
                editor.FinishNumericEdit();
                CheckRevision(revision);
                RequireTiming();
                string source = document.Path ?? throw new InvalidOperationException("Save the chart before exporting.");
                string output = AbsoluteFile(options.OutputPath, ".mp4");
                if (!options.Overwrite && File.Exists(output))
                    throw new IOException("The output file already exists.");
                if (options.Width is < 16 or > 4096 || options.Height is < 16 or > 4096 ||
                    (options.Width & 1) != 0 || (options.Height & 1) != 0 ||
                    (long)options.Width * options.Height > 4096L * 2160 || options.Fps is < 1 or > 120 ||
                    !double.IsFinite(options.PrerollSeconds) || options.PrerollSeconds is < 0 or > 10)
                    throw new ArgumentException("Use even dimensions, at most 4096 × 2160 pixels, 1–120 FPS and 0–10 seconds of preroll.");
                if (editor._timelineDocument != document || editor._chartTimeline.PlaybackRange is not { IsValid: true } range)
                    throw new InvalidOperationException("Set an A–B selection before exporting.");
                ValidateRange(new(range.Start, range.End), editor._playback!.Duration);
                if (editor._videoExport?.Request is not null || editor._modFrameJob is not null || editor._modPerformanceJob is not null)
                    throw new InvalidOperationException("Wait for the current capture, profile or export to finish.");
                var snapshot = document.SnapshotForPlayback();
                var request = new VideoExporter.Request(source, output, options.Width, options.Height, options.Fps,
                    false, 0, 0, false, options.PreferHardware, range, options.PrerollSeconds, options.Overwrite);
                editor.StartVideoJob(request, snapshot);
                return editor._modVideoJob = new(Guid.NewGuid(), revision, output, "running", 0, 0, 0, null);
            }, token);

        public ValueTask<ChartVideoJob> VideoStatusAsync(Guid job, bool cancel, CancellationToken token) =>
            ui.Dispatch(() =>
            {
                if (editor._modVideoJob is not { } active || active.Id != job)
                    throw new ArgumentException("No such export job. The editor retains its most recent MCP export.");
                if (cancel && active.State == "running")
                {
                    editor.CancelVideoRender();
                    return active with { State = "cancelling" };
                }
                return active;
            }, token);
    }

    private void TrackModVideoJob(VideoExportSession.VideoExportStatus status)
    {
        if (_modVideoJob is not { State: "running" } job) return;
        string state = status.Active ? "running" : status.Cancelled ? "cancelled" :
            status.Error is not null ? "failed" : "completed";
        _modVideoJob = job with { State = state, Frame = status.Frame, FrameCount = status.FrameCount,
            Progress = status.Progress, Error = status.Error };
    }
}
