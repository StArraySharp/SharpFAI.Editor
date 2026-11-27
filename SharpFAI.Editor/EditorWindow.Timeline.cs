using ImGui4NET;
using OpenTK.Mathematics;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly Timeline _chartTimeline = new() { TrackCount = 1 };
    private bool _showTimeline = true;
    private PlaybackRange? _loopingSelection;
    private (int First, int Last)? _timelineSelectedFloors;
    private double? _rangePreviewTime;
    private EditorDocument? _timelineDocument;
    private LevelRenderer.PlaybackTimeline? _timelineTiming;
    private EditorBeatClock? _beatClock;
    private double _timelineDuration;
    private int _timelineFloor;
    private int? _timelineRangeAnchor;
    private double _lastTimelinePosition = double.NaN;
    private double _meterBeat;
    private int _meterNumerator = 4;
    private string? _meterNumeratorDraft;
    private int _meterDenominator = 4;
    private float TimelineRulerHeight => Math.Max(38 * _uiScale, 2 * ImGui.GetTextLineHeight() + 8);
    private float TimelineFloorHeight => Math.Max(21 * _uiScale, ImGui.GetTextLineHeight() + 4);
    private float TimelineContentHeight => TimelineHeight(TimelineTrackCount);
    private float TimelineHeight(int trackCount) => TimelineRulerHeight + TimelineFloorHeight * trackCount;
    private bool HasTimelineSnapshot => _timelineDocument == _document && _timelineTiming is not null;

    private void DrawChartTimeline(LevelRenderer? renderer, bool editing, int trackCount)
    {
        if (!_showTimeline)
        {
            EndTimelineRangePreview();
            return;
        }

        _chartTimeline.RulerHeight = TimelineRulerHeight;
        _chartTimeline.TrackHeight = TimelineFloorHeight;
        _chartTimeline.TrackCount = trackCount;
        if (renderer?.EditorTiming is { } currentTiming)
            UpdateTimelineSnapshot(renderer, currentTiming, editing);
        if (!HasTimelineSnapshot)
        {
            EndTimelineRangePreview();
            var origin = ImGui.GetCursorScreenPos();
            var size = WidgetDraw.V2(Math.Max(1, ImGui.GetContentRegionAvail().x), TimelineHeight(trackCount));
            var end = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);
            var draw = ImGui.GetWindowDrawList();
            WidgetDraw.Panel(draw, origin, end, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 4);
            ImGui.Dummy(size);
            return;
        }

        var timing = _timelineTiming!;
        double duration = _timelineDuration;
        bool ready = renderer is not null;
        var clock = _beatClock!;
        var meters = _document.TimeSignatures;
        double position = renderer?.PlayTime ?? _chartTimeline.Playhead;
        double beat = Math.Max(0, clock.BeatAt(position));
        var current = meters.Changes[meters.IndexAt(beat)].Signature;
        int selectedFloor = _timelineFloor;
        bool canSeek = ready && _playbackView != PlaybackView.Manual;

        string timeText = Timeline.FormatPosition(position);
        ContinueToolbar(ImGui.CalcTextSize(timeText).x);
        ImGui.AlignTextToFramePadding();
        WidgetDraw.TextDisabled(timeText);
        if ((uint)selectedFloor < (uint)timing.EntryTimes.Length)
        {
            string floorText = L.F("TimelineFloorNumber", selectedFloor);
            ContinueToolbar(ImGui.CalcTextSize(floorText).x);
            ImGui.AlignTextToFramePadding();
            WidgetDraw.TextDisabled(floorText);
        }
        var (measure, beatInMeasure) = meters.PositionAt(beat);
        string measureText = L.F("TimelineMeasure", measure, beatInMeasure);
        ContinueToolbar(ImGui.CalcTextSize(measureText).x);
        ImGui.AlignTextToFramePadding();
        WidgetDraw.TextDisabled(measureText);
        string meterLabel = current + "###edit-time-signature";
        ContinueToolbar(ButtonWidth(meterLabel));
        bool canEditMeter = canSeek && renderer?.IsPlaying == false;
        if (!canEditMeter) ImGui.BeginDisabled(1);
        if (ImGui.Button(meterLabel, default) != 0)
        {
            _meterBeat = meters.Snap(beat);
            if (clock.TimeAt(_meterBeat) > duration + 1e-7)
                _meterBeat = meters.Snap(beat, previousOnly: true);
            var signature = meters.Changes[meters.IndexAt(_meterBeat)].Signature;
            (_meterNumerator, _meterDenominator) = (signature.Numerator, signature.Denominator);
            _meterNumeratorDraft = null;
            ImGui.OpenPopup("##chart-time-signature", 0);
        }
        WidgetDraw.ItemTooltip(L.T("TimeSignature"));
        if (!canEditMeter) ImGui.EndDisabled();
        DrawTimeSignaturePopup(clock, meters, ready);

        ContinueToolbar(ButtonWidth(L.T("TimelineFit")));
        if (ImGui.Button(L.T("TimelineFit"), default) != 0)
        {
            _chartTimeline.ViewStart = 0;
            _chartTimeline.ViewLength = Math.Max(Timeline.MinimumViewLength, duration);
        }
        ImGui.BeginDisabled((byte)(ready ? 0 : 1));
        DrawTimelineRangeControls(duration, editing, timing.EntryTimes, canSeek);
        ImGui.EndDisabled();
        _chartTimeline.Playhead = position;
        PlaybackRange? previousRange = _chartTimeline.PlaybackRange;
        bool moved = _chartTimeline.DrawChart("chart", duration, clock, meters, timing.EntryTimes, _timelineEventTracks, selectedFloor,
            _timelineRangeAnchor, editing, canSeek, renderer?.IsPlaying == true, _lastTimelinePosition, out int floor, out bool extend);
        if (renderer is not null)
        {
            if (moved)
            {
                SeekTimeline(renderer, _chartTimeline.Playhead, editing, floor, extend);
                if (editing) SyncSelectedFloorRange(timing.EntryTimes, duration);
            }
            if (_chartTimeline.RangeClearedBySeek)
                ClearTimelineRange();
            if (_loopingSelection is not null && _chartTimeline.PlaybackRange != previousRange)
                PauseToPreview();
            UpdateTimelineRangePreview(renderer, _chartTimeline.RangeDragTarget);
        }
        else
            EndTimelineRangePreview();
        DrawTimelineRangeContext(ready);
        if (ready) _modUi?.DrawContext(SharpFAI.ModApi.MenuSlot.TimelineContext, "###mod-timeline-context");
        DrawTimelineEventSelection(renderer, editing);
        _lastTimelinePosition = _chartTimeline.Playhead;
    }

    private void UpdateTimelineSnapshot(LevelRenderer renderer, LevelRenderer.PlaybackTimeline timing, bool editing)
    {
        _timelineDuration = Math.Max(0, renderer.Duration);
        if (_timelineDocument != _document)
        {
            _timelineDocument = _document;
            _timelineTiming = null;
            _chartTimeline.PlaybackRange = null;
            _timelineSelectedFloors = null;
            _chartTimeline.ViewStart = 0;
            _chartTimeline.ViewLength = Math.Max(Timeline.MinimumViewLength, _timelineDuration);
            _lastTimelinePosition = double.NaN;
        }
        if (!ReferenceEquals(_timelineTiming, timing))
        {
            _timelineTiming = timing;
            _timelineSelectedFloors = null;
            _beatClock = new EditorBeatClock(timing.EntryTimes, timing.FloorBpms, timing.SnapBpm);
        }
        if (_chartTimeline.PlaybackRange is { } range)
            _chartTimeline.PlaybackRange = range.Limit(_timelineDuration);
        if (editing) SyncSelectedFloorRange(timing.EntryTimes, _timelineDuration);
        _timelineFloor = editing ? _hasFloorSelection && !_inspectDecoration ? _floor : -1 : renderer.CurrentIndex;
        _timelineRangeAnchor = editing && _timelineFloor >= 0 ? _rangeAnchor : null;
        UpdateTimelineEventTracks(timing, _timelineDuration);
    }

    private void SyncSelectedFloorRange(ReadOnlySpan<double> times, double duration)
    {
        var selection = _hasFloorSelection && !_inspectDecoration && _rangeAnchor.HasValue
            ? FloorRange : ((int First, int Last)?)null;
        if (selection == _timelineSelectedFloors) return;
        _timelineSelectedFloors = selection;
        if (selection is { } tiles && (uint)tiles.Last < (uint)times.Length)
            _chartTimeline.PlaybackRange = RangeForTiles(times, tiles.First, tiles.Last, duration);
    }

    private static PlaybackRange RangeForTiles(ReadOnlySpan<double> times, int first, int last, double duration)
    {
        int next = TimelineFloorTimes.LowerBound(times, Math.BitIncrement(times[last]), last + 1);
        return new PlaybackRange(Math.Max(0, times[first]), next < times.Length ? times[next] : duration).Limit(duration);
    }

    private void UpdateTimelineRangePreview(LevelRenderer renderer, double? target)
    {
        if (target is not double time)
        {
            EndTimelineRangePreview();
            return;
        }
        if (_rangePreviewTime is null)
        {
            if (_playbackView == PlaybackView.Edit)
                SwitchPlaybackView(PlaybackView.Preview);
            else
                PauseToPreview();
            _playbackFloor = null;
            renderer.SetProgressScrubbing(true);
        }
        if (_rangePreviewTime != time)
        {
            _rangePreviewTime = time;
            renderer.Seek(time);
        }
    }

    private void EndTimelineRangePreview()
    {
        if (_rangePreviewTime is null) return;
        _rangePreviewTime = null;
        _playback?.SetProgressScrubbing(false);
    }

    private void DrawTimelineRangeContext(bool ready)
    {
        const string context = "##timeline-range-context";
        if (ready && _chartTimeline.HoveringPlaybackRange && ImGui.GetIO()->KeyShift == 0 &&
            ImGui.IsItemClicked((int)ImGuiMouseButton.ImGuiMouseButton_Right) != 0)
            ImGui.OpenPopup(context, 0);
        if (ImGui.BeginPopup(context, 0) == 0) return;
        if (!ready) ImGui.CloseCurrentPopup();
        else if (_chartTimeline.PlaybackRange is { IsValid: true } range && ImGui.MenuItem(L.T("TimelineExportRange")) != 0)
            OpenVideoRender(range);
        ImGui.EndPopup();
    }

    private void ClearTimelineRange()
    {
        _chartTimeline.PlaybackRange = null;
        _timelineSelectedFloors = null;
        _rangeAnchor = null;
        if (_savedEditSelection is { } saved)
            _savedEditSelection = saved with { RangeAnchor = null };
        if (_loopingSelection is not null) PauseToPreview();
    }

    private void DrawTimelineRangeControls(double duration, bool editing, ReadOnlySpan<double> floorTimes, bool canSeek)
    {
        string loop = L.T(_loopingSelection is null ? "TimelineLoopRange" : "TimelineStopLoop");
        string export = L.T("TimelineExportRange");
        ContinueToolbar(ButtonWidth("A–B"));
        if (ImGui.Button("A–B", default) != 0)
            ImGui.OpenPopup("##timeline-range", 0);
        WidgetDraw.ItemTooltip(L.T("TimelineRangeHelp"));
        if (ImGui.BeginPopup("##timeline-range", 0) != 0)
        {
            ImGui.TextUnformatted(L.T("TimelineRangeHelp"));
            var (first, last) = FloorRange;
            bool canUseTiles = editing && _hasFloorSelection && !_inspectDecoration && (uint)last < (uint)floorTimes.Length;
            if (ImGui.MenuItem(L.T("TimelineRangeFromTiles"), null, 0, (byte)(canUseTiles ? 1 : 0)) != 0)
                _chartTimeline.PlaybackRange = RangeForTiles(floorTimes, first, last, duration);
            if (_chartTimeline.PlaybackRange is { IsValid: true } selected)
            {
                ImGui.TextUnformatted(RangeLabel(selected));
                if (ImGui.MenuItem(loop, null, 0, (byte)(canSeek ? 1 : 0)) != 0)
                    ToggleTimelineLoop(selected);
                if (ImGui.MenuItem(export) != 0)
                    OpenVideoRender(selected);
            }
            if (ImGui.MenuItem(L.T("TimelineClearRange"), null, 0,
                    (byte)(_chartTimeline.PlaybackRange is null ? 0 : 1)) != 0)
                ClearTimelineRange();
            ImGui.EndPopup();
        }

        if (_chartTimeline.PlaybackRange is not { IsValid: true } range)
            return;
        string span = RangeLabel(range);
        float spanWidth = ImGui.CalcTextSize(span).x;
        float right = ImGui.GetCursorScreenPos().x + ImGui.GetContentRegionAvail().x;
        if (ImGui.GetItemRectMax().x + spanWidth + ButtonWidth(loop) + ButtonWidth(export) +
            3 * ImGui.GetStyle()->ItemSpacing.x > right)
            return;
        ContinueToolbar(spanWidth);
        ImGui.AlignTextToFramePadding();
        WidgetDraw.TextDisabled(span);

        ContinueToolbar(ButtonWidth(loop));
        ImGui.BeginDisabled((byte)(canSeek ? 0 : 1));
        if (ImGui.Button(loop, default) != 0)
            ToggleTimelineLoop(range);
        ImGui.EndDisabled();
        ContinueToolbar(ButtonWidth(export));
        if (ImGui.Button(export, default) != 0)
            OpenVideoRender(range);
    }

    private static string RangeLabel(PlaybackRange range) =>
        $"{Timeline.FormatPosition(range.Start)} – {Timeline.FormatPosition(range.End)}";

    private void ToggleTimelineLoop(PlaybackRange range)
    {
        if (_loopingSelection is not null)
        {
            PauseToPreview();
            return;
        }
        SwitchPlaybackView(PlaybackView.Auto);
        if (_startPlayback)
        {
            _loopingSelection = range;
            _playbackFloor = null;
        }
    }

    private void DrawTimeSignaturePopup(EditorBeatClock clock, EditorTimeSignatures meters, bool canEdit)
    {
        if (ImGui.BeginPopup("##chart-time-signature", 0) == 0)
            return;
        if (!canEdit)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted(L.T("TimeSignature"));
        WidgetDraw.TextDisabled(L.F("TimelineAtTime", Timeline.FormatPosition(Math.Max(0, clock.TimeAt(_meterBeat)))));
        ImGui.SetNextItemWidth(100 * _uiScale);
        bool validNumerator = DrawIntegerExpression(L.T("TimelineNumerator"), ref _meterNumerator, ref _meterNumeratorDraft, 1, out _);
        ImGui.SetNextItemWidth(100 * _uiScale);
        if (ImGui.BeginCombo(L.T("TimelineDenominator"), _meterDenominator.ToString()) != 0)
        {
            for (int denominator = 1; denominator <= TimeSignature.MaxDenominator; denominator *= 2)
                if (ImGui.Selectable(denominator.ToString(), (byte)(denominator == _meterDenominator ? 1 : 0), 0, default) != 0)
                    _meterDenominator = denominator;
            ImGui.EndCombo();
        }

        TimeSignature signature = new(_meterNumerator, _meterDenominator);
        bool valid = validNumerator && signature.IsValid;
        if (!valid) ImGui.BeginDisabled(1);
        if (ImGui.Button(L.T("TimelineApplyHere"), default) != 0)
        {
            _document.SetTimeSignature(_meterBeat, signature);
            ImGui.CloseCurrentPopup();
        }
        if (_meterBeat > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button(L.T("TimelineApplyAtStart"), default) != 0)
            {
                _document.SetTimeSignature(0, signature);
                ImGui.CloseCurrentPopup();
            }
        }
        if (!valid) ImGui.EndDisabled();
        var existing = meters.Changes[meters.IndexAt(_meterBeat)];
        if (existing.Beat == _meterBeat && (_meterBeat != 0 || existing.Signature != TimeSignature.Common))
        {
            ImGui.SameLine();
            if (ImGui.Button(L.T("TimelineRemoveSignature"), default) != 0)
            {
                _document.SetTimeSignature(_meterBeat, null);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private void SeekTimeline(LevelRenderer renderer, double seconds, bool editing, int marker, bool extend = false)
    {
        if (!editing)
        {
            PauseToPreview();
            if (marker >= 0)
            {
                SelectFloor(marker, false);
                _playbackFloor = null;
                renderer.SelectFloor(marker);
            }
            else
                renderer.Seek(seconds);
            return;
        }

        var camera = (Position: renderer.Camera.Position, Zoom: renderer.Camera.Zoom);
        if (marker >= 0)
            renderer.SelectFloor(marker);
        else
            renderer.Seek(seconds);
        int floor = marker >= 0 ? marker : renderer.CurrentIndex;
        if (marker >= 0 || _floor != floor || !_hasFloorSelection || _inspectDecoration)
        {
            SelectFloorFromView(floor, extend);
            _playbackFloor = null;
            if (renderer.TryGetFloorCenter(_floor, out Vector2 center))
                camera.Position = center;
        }
        RestoreEditCamera(renderer, camera);
    }
}
