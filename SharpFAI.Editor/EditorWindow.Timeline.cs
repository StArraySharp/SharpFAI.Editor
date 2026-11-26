using ImGui4NET;
using OpenTK.Mathematics;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private readonly Timeline _chartTimeline = new() { TrackCount = 1 };
    private bool _showTimeline = true;
    private EditorDocument? _timelineDocument;
    private LevelRenderer.PlaybackTimeline? _timelineTiming;
    private EditorBeatClock? _beatClock;
    private double _lastTimelinePosition = double.NaN;
    private double _meterBeat;
    private int _meterNumerator = 4;
    private string? _meterNumeratorDraft;
    private int _meterDenominator = 4;
    private float TimelineRulerHeight => Math.Max(42 * _uiScale, 2 * ImGui.GetTextLineHeight() + 12);
    private float TimelineFloorHeight => Math.Max(23 * _uiScale, ImGui.GetTextLineHeight() + 8);

    private void DrawChartTimeline(LevelRenderer? renderer, bool editing)
    {
        if (!_showTimeline)
            return;

        _chartTimeline.RulerHeight = TimelineRulerHeight;
        _chartTimeline.TrackHeight = TimelineFloorHeight;
        if (renderer?.EditorTiming is not { } timing || !double.IsFinite(renderer.Duration))
        {
            ImGui.Dummy(WidgetDraw.V2(1, _chartTimeline.RulerHeight + _chartTimeline.TrackHeight));
            return;
        }
        double duration = Math.Max(0, renderer.Duration);
        if (_timelineDocument != _document)
        {
            _timelineDocument = _document;
            _timelineTiming = null;
            _chartTimeline.ViewStart = 0;
            _chartTimeline.ViewLength = Math.Max(.05, duration);
            _lastTimelinePosition = double.NaN;
        }
        if (!ReferenceEquals(_timelineTiming, timing))
        {
            _timelineTiming = timing;
            _beatClock = new EditorBeatClock(timing.EntryTimes, timing.FloorBpms, timing.SnapBpm);
        }

        var clock = _beatClock!;
        var meters = _document.TimeSignatures;
        double position = renderer.PlayTime;
        double beat = Math.Max(0, clock.BeatAt(position));
        var current = meters.Changes[meters.IndexAt(beat)].Signature;
        int selectedFloor = editing ? _hasFloorSelection && !_inspectDecoration ? _floor : -1 : renderer.CurrentIndex;
        bool canSeek = _playbackView != PlaybackView.Manual;

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
        bool canEditMeter = canSeek && !renderer.IsPlaying;
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
        DrawTimeSignaturePopup(clock, meters);

        ContinueToolbar(ButtonWidth(L.T("TimelineFit")));
        if (ImGui.Button(L.T("TimelineFit"), default) != 0)
        {
            _chartTimeline.ViewStart = 0;
            _chartTimeline.ViewLength = Math.Max(.05, duration);
        }
        _chartTimeline.Playhead = position;
        if (_chartTimeline.DrawChart("chart", duration, clock, meters, timing.EntryTimes, selectedFloor, canSeek,
                renderer.IsPlaying, _lastTimelinePosition, out int floor))
            SeekTimeline(renderer, _chartTimeline.Playhead, editing, floor);
        _modUi?.DrawContext(SharpFAI.ModApi.MenuSlot.TimelineContext, "###mod-timeline-context");
        _lastTimelinePosition = _chartTimeline.Playhead;
    }

    private void DrawTimeSignaturePopup(EditorBeatClock clock, EditorTimeSignatures meters)
    {
        if (ImGui.BeginPopup("##chart-time-signature", 0) == 0)
            return;

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

    private void SeekTimeline(LevelRenderer renderer, double seconds, bool editing, int marker)
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
            SelectFloor(floor, false);
            _playbackFloor = null;
            if (renderer.TryGetFloorCenter(_floor, out Vector2 center))
                camera.Position = center;
        }
        RestoreEditCamera(renderer, camera);
    }
}
