using System.Diagnostics;
using SharpFAI.ModApi;
using SharpFAI.Player;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private sealed partial class ModEditorInspection
    {
        public ValueTask<ChartEditPreview> PreviewEditAsync(ChartRevision revision, ChartEditRequest edit, CancellationToken token) =>
            ui.Dispatch(() =>
            {
                List<ChartEditChange> changes = [];
                var document = CheckRevision(revision);
                bool valid = document.StageModEdits(edit, changes, out int rejected) is not null;
                return new ChartEditPreview(revision, valid, valid ? null : rejected,
                    valid ? null : "The indexed operation is invalid for the chart after preceding operations.", changes);
            }, token);

        public ValueTask<ChartEditCommit> CommitEditAsync(ChartRevision revision, ChartEditRequest edit, CancellationToken token) =>
            ui.Dispatch(() =>
            {
                var result = editor.CommitModEdit(revision, edit);
                return new ChartEditCommit(editor._document.ModRevision, result);
            }, token);

        public ValueTask<ChartFileResult> OpenAsync(ChartRevision revision, string path, bool discardUnsaved,
            CancellationToken token) => ui.Dispatch(() =>
        {
            CheckRevision(revision);
            string file = AbsoluteFile(path, ".adofai", ".json");
            editor.FinishNumericEdit();
            CheckRevision(revision);
            if (editor._document.Modified && !discardUnsaved)
                throw new InvalidOperationException("The open chart has unsaved changes. Save it first or explicitly allow discarding them.");
            long started = Stopwatch.GetTimestamp();
            var document = new EditorDocument(file);
            editor.SetDocument(document, started);
            return new ChartFileResult(file, document.ModRevision, document.Modified);
        }, token);

        public ValueTask<ChartFileResult> SaveAsync(ChartRevision revision, string path, bool overwrite,
            CancellationToken token) => ui.Dispatch(() =>
        {
            CheckRevision(revision);
            string file = AbsoluteFile(path, ".adofai", ".json");
            editor.FinishNumericEdit();
            var document = CheckRevision(revision);
            bool samePath = string.Equals(file, document.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            editor.SaveToPath(file, overwrite || samePath);
            return new ChartFileResult(file, document.ModRevision, document.Modified);
        }, token);

        public ValueTask<ChartTempo> ReadTempoAsync(ChartRevision revision, int floor, CancellationToken token) =>
            ui.Dispatch(() =>
            {
                CheckRevision(revision);
                return ReadTempo(RequireTiming(), floor);
            }, token);

        public ValueTask<ChartSpeedConversion> ConvertTempoAsync(ChartRevision revision, int floor, int eventIndex,
            double targetBpm, CancellationToken token) => ui.Dispatch(() =>
        {
            var document = CheckRevision(revision);
            var timing = RequireTiming();
            var tempo = ReadTempo(timing, floor);
            var events = document.Events(floor);
            if ((uint)eventIndex >= (uint)events.Count ||
                !SetSpeedTempo.TryConvert(timing, floor, tempo.Pitch, events, events[eventIndex], targetBpm, out var result))
                throw new ArgumentException("The event cannot reach that effective BPM with its current speed mode and angle offset.");
            var field = editor._catalog.Find("SetSpeed")?.Fields.FirstOrDefault(candidate => candidate.Key == result.Key);
            if (result.Value < field?.Minimum || result.Value > field?.Maximum)
                throw new ArgumentException("The converted value is outside the event field range.");
            return new ChartSpeedConversion(tempo, eventIndex, result.Key, result.Value, targetBpm);
        }, token);

        public ValueTask<ChartSelection> SelectAsync(ChartRevision revision, ChartSelectionAction action,
            ChartTimeRange? range, CancellationToken token) => ui.Dispatch(() =>
        {
            CheckRevision(revision);
            if ((action == ChartSelectionAction.Set) != (range is not null))
                throw new ArgumentException("Supply start and end only with the set action.");
            switch (action)
            {
                case ChartSelectionAction.Read:
                    break;
                case ChartSelectionAction.Set:
                    var timing = RequireTiming();
                    var next = ValidateRange(range!, editor._playback!.Duration);
                    editor.UpdateTimelineSnapshot(editor._playback, timing, editor._playbackView == PlaybackView.Edit);
                    editor.ClearTimelineRange();
                    editor._chartTimeline.PlaybackRange = next;
                    break;
                case ChartSelectionAction.Clear:
                    editor.ClearTimelineRange();
                    break;
                case ChartSelectionAction.Loop:
                    RequireTiming();
                    if (editor._timelineDocument != editor._document ||
                        editor._chartTimeline.PlaybackRange is not { IsValid: true } selected)
                        throw new InvalidOperationException("Set an A–B selection before starting a loop.");
                    if (editor._videoExport?.Request is not null || editor._modPerformanceJob is not null ||
                        editor._playbackView == PlaybackView.Manual)
                        throw new InvalidOperationException("Loop playback is unavailable in the current editor mode.");
                    if (editor._loopingSelection is null) editor.ToggleTimelineLoop(selected);
                    break;
                case ChartSelectionAction.StopLoop:
                    if (editor._loopingSelection is not null) editor.PauseToPreview();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
            return new ChartSelection(revision, editor._timelineDocument == editor._document &&
                editor._chartTimeline.PlaybackRange is { IsValid: true } value ? new(value.Start, value.End) : null,
                editor._loopingSelection is not null);
        }, token);

        private LevelRenderer.PlaybackTimeline RequireTiming() =>
            Timing ?? throw new InvalidOperationException("Chart timing is being synchronized. Retry shortly.");

        private ChartTempo ReadTempo(LevelRenderer.PlaybackTimeline timing, int floor)
        {
            double pitch = LevelRenderer.ReadPlaybackPitch(editor._document.Settings);
            var value = timing.TempoAt(floor, pitch) ?? throw new ArgumentOutOfRangeException(nameof(floor));
            return new(floor, value.Authored, value.Effective, pitch);
        }
    }

    private static string AbsoluteFile(string path, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Provide an absolute path ending in {string.Join(" or ", extensions)}.");
        return Path.GetFullPath(path);
    }

    private static PlaybackRange ValidateRange(ChartTimeRange range, double duration)
    {
        var value = new PlaybackRange(range.Start, range.End);
        if (!value.IsValid || !double.IsFinite(duration) || range.End > duration)
            throw new ArgumentException($"Use a range satisfying 0 ≤ start < end ≤ {duration:G6} seconds.");
        return value;
    }
}
