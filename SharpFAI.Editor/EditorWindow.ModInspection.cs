using Newtonsoft.Json.Linq;
using SharpFAI.ModApi;
using SharpFAI.Player;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed partial class EditorWindow
{
    private sealed class ModEditorInspection(EditorWindow editor, ModUi ui) : IEditorInspection, IEditorFrameCapture
    {
        private const int ScanLimit = 4096;
        private LevelRenderer.PlaybackTimeline? _timing;
        private EditorBeatClock? _clock;

        public ValueTask<ChartContext> GetChartAsync(CancellationToken token) => ui.Dispatch(() =>
        {
            var document = editor._document;
            int? selectedFloor = editor._hasFloorSelection && !editor._inspectDecoration ? editor._floor : null;
            int decorationIndex = editor._decoration is { } selected ? document.Decorations.IndexOf(selected) : -1;
            return new ChartContext(document.ModRevision, Path.GetFileName(document.Path), document.Modified,
                document.LastFloor, selectedFloor, decorationIndex >= 0 ? decorationIndex : null, document.Decorations.Count,
                editor.ModContext(false).Playback, Timing is not null, document.Settings.Properties()
                    .ToDictionary(property => property.Name, property => EditorDocument.ToElement(property.Value)));
        }, token);

        public ValueTask<ChartPosition> LocateAsync(ChartRevision expected, int? floor, double? seconds,
            double? quarterNote, CancellationToken token) => ui.Dispatch(() =>
        {
            CheckRevision(expected);
            if ((floor.HasValue ? 1 : 0) + (seconds.HasValue ? 1 : 0) + (quarterNote.HasValue ? 1 : 0) != 1)
                throw new ArgumentException("Provide one of floor, seconds, or quarterNote.");
            var timing = Timing ?? throw new InvalidOperationException("Chart timing is being synchronized. Retry shortly.");
            var clock = Clock(timing);
            if (floor is int index)
            {
                if ((uint)index >= (uint)timing.EntryTimes.Length) throw new ArgumentOutOfRangeException(nameof(floor));
                return Position(timing, clock, index, timing.EntryTimes[index]);
            }
            double time = seconds ?? clock.TimeAt(quarterNote!.Value);
            if (!double.IsFinite(time)) throw new ArgumentException("Position must be finite.");
            return Position(timing, clock, LevelRenderer.FloorAtTime(timing.EntryTimes, time), time);
        }, token);

        public ValueTask<IReadOnlyList<ChartFloor>> ReadFloorsAsync(ChartRevision expected, int firstFloor, int count,
            CancellationToken token) => ui.Dispatch<IReadOnlyList<ChartFloor>>(() =>
        {
            var document = CheckRevision(expected);
            if (firstFloor < 0 || firstFloor > document.LastFloor || count is < 1 or > 512)
                throw new ArgumentOutOfRangeException(nameof(count), "Use an existing floor and a count from 1 through 512.");
            var timing = Timing;
            var clock = timing is null ? null : Clock(timing);
            return Enumerable.Range(firstFloor, Math.Min(count, document.LastFloor - firstFloor + 1))
                .Select(floor => new ChartFloor(floor, floor > 0 ? document.ModViewAngle(floor) : null,
                    timing is not null && floor < timing.EntryTimes.Length
                        ? Position(timing, clock!, floor, timing.EntryTimes[floor]) : null)).ToArray();
        }, token);

        public ValueTask<ChartEventPage> ReadEventsAsync(ChartRevision expected, ChartEventCursor start, int lastFloor,
            string? type, string? tag, int limit, CancellationToken token) => ui.Dispatch(() =>
        {
            var document = CheckRevision(expected);
            if (start.Floor < 0 || start.Floor > lastFloor || lastFloor > document.LastFloor ||
                start.Index < 0 || limit is < 1 or > 256)
                throw new ArgumentException("Use a valid floor range, cursor, and a limit from 1 through 256.");
            List<ChartEventItem> items = [];
            int scanned = 0;
            for (int floor = start.Floor; floor <= lastFloor; floor++)
            {
                int first = floor == start.Floor ? start.Index : 0;
                if (++scanned > ScanLimit || items.Count == limit) return new ChartEventPage(expected, items, new(floor, first));
                var events = document.Events(floor);
                for (int index = first; index < events.Count; index++)
                {
                    if (++scanned > ScanLimit || items.Count == limit) return new ChartEventPage(expected, items, new(floor, index));
                    var item = events[index];
                    if (Matches(item, type, tag)) items.Add(new(floor, index, EditorDocument.ToElement(item)));
                }
            }
            return new ChartEventPage(expected, items, null);
        }, token);

        public ValueTask<ChartDecorationPage> ReadDecorationsAsync(ChartRevision expected, int startIndex, string? type,
            string? tag, int limit, CancellationToken token) => ui.Dispatch(() =>
        {
            var decorations = CheckRevision(expected).Decorations;
            if (startIndex < 0 || startIndex > decorations.Count || limit is < 1 or > 256)
                throw new ArgumentException("Use a valid decoration index and a limit from 1 through 256.");
            List<ChartDecorationItem> items = [];
            for (int index = startIndex; index < decorations.Count; index++)
            {
                if (index - startIndex == ScanLimit || items.Count == limit) return new ChartDecorationPage(expected, items, index);
                if (decorations[index] is JObject item && Matches(item, type, tag))
                    items.Add(new(index, EditorDocument.ToElement(item)));
            }
            return new ChartDecorationPage(expected, items, null);
        }, token);

        public ValueTask<ChartDiagnostics> GetDiagnosticsAsync(ChartRevision expected, CancellationToken token) =>
            ui.Dispatch(() =>
            {
                CheckRevision(expected);
                bool current = editor.HasCurrentPlayback;
                return new ChartDiagnostics(expected, current,
                    editor._failedRevision == editor.CurrentPlaybackRevision ? editor._playback?.LoadError : null,
                    current ? editor._playback!.Warnings.ToArray() : []);
            }, token);

        public ValueTask<ChartEventSchema?> GetEventSchemaAsync(string type, CancellationToken token) =>
            ui.Dispatch<ChartEventSchema?>(() =>
            {
                var item = editor._catalog.Find(type);
                return item is null ? null : new ChartEventSchema(item.Type, item.Decoration, item.AllowFirstFloor,
                    item.Fields.Where(field => !field.Hidden).Select(field => new ChartFieldSchema(field.Key,
                        field.Type, field.Default is null ? null : EditorDocument.ToElement(field.Default),
                        field.Optional, field.Unit, field.Minimum, field.Maximum, field.Options)).ToArray());
            }, token);

        public async ValueTask<ChartFrameCapture> CaptureAsync(FrameCaptureRequest request, CancellationToken token)
        {
            var job = await ui.Dispatch(() => editor.StartModFrameCapture(request, CheckRevision(request.Revision), token), token);
            if (job is null) throw new InvalidOperationException("The editor is closing.");
            var frame = await job.Completion.Task.WaitAsync(token);
            if (frame.Pixels is null) return new(request.Revision, request.Width, request.Height, frame.State, null);
            byte[] png = await Task.Run(() => FrameCapture.EncodePng(frame.Pixels, request.Width, request.Height), token);
            return new(request.Revision, request.Width, request.Height, frame.State, png);
        }

        private LevelRenderer.PlaybackTimeline? Timing => editor.HasCurrentPlayback &&
            editor._playback?.EditorTiming is { EntryTimes.Length: > 0 } timing ? timing : null;

        private EditorDocument CheckRevision(ChartRevision expected)
        {
            var document = editor._document;
            if (document.ModRevision != expected)
                throw new InvalidOperationException("Chart revision changed. Request the current chart and retry.");
            return document;
        }

        private EditorBeatClock Clock(LevelRenderer.PlaybackTimeline timing)
        {
            if (!ReferenceEquals(_timing, timing))
            {
                _clock = new(timing.EntryTimes, timing.FloorBpms, timing.SnapBpm);
                _timing = timing;
            }
            return _clock!;
        }

        private ChartPosition Position(LevelRenderer.PlaybackTimeline timing, EditorBeatClock clock, int floor, double seconds)
        {
            double beat = clock.BeatAt(seconds);
            var meters = editor._document.TimeSignatures;
            var (measure, beatInMeasure) = meters.PositionAt(Math.Max(0, beat));
            var signature = meters.Changes[meters.IndexAt(beat)].Signature;
            return new(floor, seconds, beat, measure, beatInMeasure, timing.FloorBpms[floor],
                signature.Numerator, signature.Denominator);
        }

        private static bool Matches(JObject item, string? type, string? tag) =>
            (type is null || item["eventType"]?.ToString() == type) &&
            (tag is null || HasTag(item["tag"], tag) || HasTag(item["eventTag"], tag));

        private static bool HasTag(JToken? value, string tag) =>
            value is JValue { Type: JTokenType.String } &&
            value.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(tag, StringComparer.Ordinal);
    }
}
