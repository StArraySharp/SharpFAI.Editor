using System.Globalization;
using ImGui4NET;
using OpenTK.Mathematics;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor.Widgets;

/// <summary>
/// A single clip on the timeline.
/// </summary>
public sealed class TimelineClip
{
    public TimelineClip(string label, double start, double duration, Vector4 color)
    {
        Label = label;
        Start = start;
        Duration = duration;
        Color = color;
    }

    public string Label { get; set; }

    /// <summary>Start time in seconds.</summary>
    public double Start { get; set; }

    /// <summary>Duration in seconds.</summary>
    public double Duration { get; set; }

    public Vector4 Color { get; set; }

    /// <summary>Track index this clip belongs to.</summary>
    public int Track { get; set; }

    public object? UserData { get; set; }
}

/// <summary>
/// Zoomable, pannable timeline with ticks, tracks and clips, including drag and snapping.
/// </summary>
public sealed unsafe partial class Timeline
{
    internal const double MinimumViewLength = .005;
    private const float BeatBandStart = .53f;
    private static readonly double[] TickSteps =
        [.001, .002, .005, .01, .02, .05, .1, .25, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
    private readonly List<TimelineClip> _clips = [];
    private readonly Dictionary<TimelineClip, (double Start, double GrabOffset)> _drag = [];
    private double? _rangeDragStart;
    private bool _rangeDragMoved;
    private bool _floorRangeGesture;
    internal PlaybackRange? PlaybackRange { get; set; }
    private bool SelectingPlaybackRange => _rangeDragStart.HasValue;
    internal double? RangeDragTarget { get; private set; }
    internal bool RangeClearedBySeek { get; private set; }
    internal bool HoveringPlaybackRange { get; private set; }

    /// <summary>Whether clips were modified last frame.</summary>
    public bool ClipsModified { get; private set; }

    /// <summary>Left edge of the visible range, in seconds.</summary>
    public double ViewStart { get; set; }

    /// <summary>Length of the visible range, in seconds.</summary>
    public double ViewLength { get; set; } = 10.0;

    /// <summary>Number of tracks.</summary>
    public int TrackCount { get; set; } = 3;

    /// <summary>Height of each track row.</summary>
    public float TrackHeight { get; set; } = 26f;

    /// <summary>Height of the ruler band.</summary>
    public float RulerHeight { get; set; } = 20f;

    /// <summary>Snap interval while dragging; 0 disables snapping.</summary>
    public double SnapInterval { get; set; } = 0.25;

    /// <summary>Playhead position in seconds.</summary>
    public double Playhead { get; set; }

    /// <summary>Whether the wheel zooms the view.</summary>
    public bool AllowZoom { get; set; } = true;

    /// <summary>The clip selected by the user.</summary>
    public TimelineClip? Selected { get; private set; }

    public IReadOnlyList<TimelineClip> Clips => _clips;

    public void Add(TimelineClip clip)
    {
        _clips.Add(clip);
    }

    public bool Remove(TimelineClip clip) => _clips.Remove(clip);

    public void Clear()
    {
        _clips.Clear();
        _drag.Clear();
        Selected = null;
    }

    /// <summary>Fit the view around every clip.</summary>
    public void FitToClips(double padding = 0.1)
    {
        if (_clips.Count == 0)
        {
            return;
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (TimelineClip clip in _clips)
        {
            min = Math.Min(min, clip.Start);
            max = Math.Max(max, clip.Start + clip.Duration);
        }

        double span = Math.Max(max - min, 0.001);
        ViewStart = min - span * padding;
        ViewLength = span * (1.0 + padding * 2.0);
    }

    /// <summary>Center the view on the playhead.</summary>
    public void CenterOnPlayhead()
    {
        ViewStart = Playhead - ViewLength * 0.5;
    }

    /// <summary>
    /// Draw the timeline. Returns true when the playhead moved this frame.
    /// </summary>
    public bool Draw(string id) => Draw(id, null, null, null, [], [], -1, null, false, true, false, double.NaN, out _, out _);

    internal bool DrawChart(string id, double duration, EditorBeatClock clock, EditorTimeSignatures signatures,
        ReadOnlySpan<double> floorTimes, ReadOnlySpan<TimelineEventTrack> eventTracks, int selectedFloor, int? rangeAnchor, bool editing, bool canSeek,
        bool playing, double previousPlayhead, out int floor, out bool extend) =>
        Draw(id, duration, clock, signatures, floorTimes, eventTracks, selectedFloor, rangeAnchor, editing, canSeek, playing,
            previousPlayhead, out floor, out extend);

    private bool Draw(string id, double? duration, EditorBeatClock? clock, EditorTimeSignatures? signatures,
        ReadOnlySpan<double> floorTimes, ReadOnlySpan<TimelineEventTrack> eventTracks, int selectedFloor, int? rangeAnchor, bool editing, bool canSeek,
        bool playing, double previousPlayhead, out int floor, out bool extend)
    {
        floor = -1;
        extend = false;
        RangeDragTarget = null;
        RangeClearedBySeek = false;
        HoveringPlaybackRange = false;
        PickedEventGroup = null;
        float height = RulerHeight + TrackHeight * TrackCount;
        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(Math.Max(1, avail.x), height);
        ImVec2 origin = ImGui.GetCursorScreenPos();

        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);

        WidgetDraw.Panel(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 4f);

        ImGui.PushID(id);
        ImGui.InvisibleButton("##timeline", in size);
        bool hovered = ImGui.IsItemHovered() != 0;
        bool active = ImGui.IsItemActive() != 0;
        ImGui.PopID();

        float gutter = LaneLabelWidth(eventTracks, size.x);
        if (gutter > 0)
        {
            DrawLaneLabels(draw, origin, gutter, eventTracks);
            origin.x += gutter;
            size.x -= gutter;
        }

        int left = (int)ImGuiMouseButton.ImGuiMouseButton_Left;
        bool clicked = ImGui.IsMouseClicked(left) != 0;
        bool dragging = canSeek && active && ImGui.IsMouseDragging(left, 4f) != 0;
        bool finishRange = canSeek && _rangeDragStart.HasValue && _rangeDragMoved && ImGui.IsMouseReleased(left) != 0;
        ImGuiIO* io = ImGui.GetIO();
        ImVec2 mouse = ImGui.GetMousePos();
        hovered &= mouse.x >= origin.x;
        bool onFloors = mouse.y >= origin.y + RulerHeight && mouse.y < origin.y + RulerHeight + TrackHeight;
        if (!canSeek || !active && !finishRange)
        {
            _rangeDragStart = null;
            _rangeDragMoved = false;
            _floorRangeGesture = false;
        }
        if (duration is double chartEnd && canSeek && active && clicked && hovered)
        {
            if (io->KeyCtrl != 0)
                _rangeDragStart = Math.Clamp(TimeAt(mouse.x, origin.x, size.x), 0, chartEnd);
            else
                _floorRangeGesture = editing && onFloors && io->KeyShift != 0;
        }
        if (!canSeek) _chartGestureStartedInPlot = false;
        else if (clicked && active) _chartGestureStartedInPlot = hovered;
        dragging &= _chartGestureStartedInPlot;
        bool playheadMoved = false;
        bool wheel = hovered && AllowZoom && MathF.Abs(io->MouseWheel) > 0.001f;

        if (wheel)
        {
            if (duration.HasValue ? io->KeyShift != 0 : io->KeyCtrl != 0)
                ViewStart -= io->MouseWheel * ViewLength * 0.05;
            else
            {
                double zoom = Math.Exp(-io->MouseWheel * 0.15);
                double newLength = Math.Clamp(ViewLength * zoom, MinimumViewLength,
                    duration is double limit ? Math.Max(MinimumViewLength, limit) : 3600.0);
                double anchor = ViewStart + Math.Clamp((mouse.x - origin.x) / size.x, 0.0, 1.0) * ViewLength;
                ViewStart = anchor - (anchor - ViewStart) * (newLength / ViewLength);
                ViewLength = newLength;
            }
        }
        if (duration is double end)
        {
            ViewLength = Math.Clamp(ViewLength, MinimumViewLength, Math.Max(MinimumViewLength, end));
            ViewStart = TimelineNavigation.ClampStart(ViewStart, ViewLength, end);
            double margin = Math.Min(.18, 64 / (double)size.x);
            if (dragging)
            {
                double pointer = (mouse.x - origin.x) / (double)size.x;
                ViewStart = TimelineNavigation.DragScroll(ViewStart, ViewLength, end, pointer, margin, io->DeltaTime);
            }
            else if (!wheel && (!active || !canSeek))
            {
                ViewStart = TimelineNavigation.KeepPlayheadInView(ViewStart, ViewLength, end,
                    Playhead, previousPlayhead, margin, playing);
            }
        }

        ImDrawList.PushClipRect(draw, origin, max, 1);
        DrawTracks(draw, in origin, in size);
        DrawWaveform(draw, origin, size.x);
        DrawRuler(draw, in origin, in size, clock is null ? RulerHeight : RulerHeight * .51f);
        if (clock is not null && signatures is not null)
        {
            DrawBeatGrid(draw, origin, size, clock, signatures);
            DrawFloorMarkers(draw, origin, size, floorTimes, selectedFloor, rangeAnchor);
            DrawEventTracks(draw, origin, size, eventTracks, hovered, canSeek && active && clicked && io->KeyShift == 0 &&
                !SelectingPlaybackRange, !SelectingPlaybackRange && !dragging);
        }
        if (_rangeDragStart is double start && duration is double chartLength && (dragging || finishRange))
        {
            _rangeDragMoved = true;
            double target = Math.Clamp(TimeAt(Math.Clamp(mouse.x, origin.x, max.x), origin.x, size.x), 0, chartLength);
            PlaybackRange = new(Math.Min(start, target), Math.Max(start, target));
            RangeDragTarget = target;
            Playhead = target;
        }
        if (duration.HasValue && PlaybackRange is { IsValid: true } range)
        {
            HoveringPlaybackRange = hovered && mouse.x >= XAt(range.Start, origin.x, size.x) - 2 &&
                mouse.x <= XAt(range.End, origin.x, size.x) + 2;
            if (canSeek && clicked && active && hovered && !SelectingPlaybackRange && !_floorRangeGesture && !HoveringPlaybackRange)
            {
                RangeClearedBySeek = true;
                PlaybackRange = null;
            }
            else DrawPlaybackRange(draw, origin, size, range);
        }
        DrawClips(draw, in origin, in size, ref playheadMoved);
        ClipsModified = playheadMoved;

        if (duration is double length && clock is not null && signatures is not null)
        {
            bool snap = io->KeyShift != 0;
            if (!SelectingPlaybackRange && (hovered && onFloors && !dragging || _floorRangeGesture && dragging))
            {
                double time = TimeAt(Math.Clamp(mouse.x, origin.x, max.x), origin.x, size.x);
                int nearest = TimelineFloorTimes.Nearest(floorTimes, time,
                    _floorRangeGesture ? double.PositiveInfinity : ViewLength * 8 / size.x, selectedFloor);
                if (nearest >= 0 && floorTimes[nearest] >= 0)
                {
                    if (!active)
                        WidgetDraw.ItemTooltip(L.F("TimelineFloorAtTime", nearest, FormatPosition(floorTimes[nearest])));
                    if (canSeek && active && (clicked && !snap || _floorRangeGesture) &&
                        (clicked || nearest != selectedFloor))
                    {
                        floor = nearest;
                        extend = _floorRangeGesture;
                    }
                }
            }
            if (canSeek && active && _chartGestureStartedInPlot && PickedEventGroup is null && !SelectingPlaybackRange && (clicked || dragging) &&
                (!_floorRangeGesture || floor >= 0))
            {
                float x = Math.Clamp(mouse.x, origin.x, max.x);
                double time = floor >= 0 ? Math.Clamp(floorTimes[floor], 0, length)
                    : TimelineNavigation.SeekTime(TimeAt(x, origin.x, size.x), length, snap, clock, signatures);
                playheadMoved = floor >= 0 || Math.Abs(time - Playhead) > 1e-7;
                Playhead = time;
                if (snap)
                {
                    double beatPosition = clock.BeatAt(time);
                    if (beatPosition >= 0)
                    {
                        var (measure, beat) = signatures.PositionAt(beatPosition);
                        ImGui.SetTooltip($"{FormatPosition(time)} · {L.F("TimelineMeasure", measure, beat)}");
                    }
                }
            }
            if (hovered && !active && (mouse.y < origin.y + RulerHeight || onFloors && editing && snap))
                WidgetDraw.ItemTooltip(L.T(onFloors ? "TimelineFloorRangeHelp" : "TimelineHelp"));
        }
        else if (hovered && clicked)
        {
            if (mouse.y - origin.y <= RulerHeight)
            {
                Playhead = TimeAt(mouse.x, origin.x, size.x);
                playheadMoved = true;
            }
        }

        DrawPlayhead(draw, in origin, in size);
        ImDrawList.PopClipRect(draw);

        ImVec2 outerOrigin = WidgetDraw.V2(origin.x - gutter, origin.y);
        WidgetDraw.Outline(draw, in outerOrigin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 4f);
        return playheadMoved;
    }

    private void DrawPlaybackRange(ImDrawList* draw, ImVec2 origin, ImVec2 size, PlaybackRange range)
    {
        float start = XAt(range.Start, origin.x, size.x);
        float end = XAt(range.End, origin.x, size.x);
        float right = origin.x + size.x;
        if (end < origin.x || start > right)
            return;
        uint color = WidgetDraw.U32(244, 181, 81);
        ImDrawList.AddRectFilled(draw, WidgetDraw.V2(Math.Max(start, origin.x), origin.y),
            WidgetDraw.V2(Math.Min(end, right), origin.y + size.y), WidgetDraw.WithAlpha(color, .21f));
        Marker(start, "A");
        Marker(end, "B");

        void Marker(float x, string label)
        {
            if (x < origin.x || x > right) return;
            ImDrawList.AddLine(draw, WidgetDraw.V2(x, origin.y), WidgetDraw.V2(x, origin.y + size.y), color, 2f);
            ImDrawList.AddText(draw, WidgetDraw.V2(x + 3, origin.y + size.y - ImGui.GetTextLineHeight()), color, label);
        }
    }

    private void DrawTracks(ImDrawList* draw, in ImVec2 origin, in ImVec2 size)
    {
        uint separator = WidgetDraw.WithAlpha(WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 0.6f);

        for (int i = 0; i <= TrackCount; i++)
        {
            float y = origin.y + RulerHeight + i * TrackHeight;
            ImVec2 a = WidgetDraw.V2(origin.x, y);
            ImVec2 b = WidgetDraw.V2(origin.x + size.x, y);
            ImDrawList.AddLine(draw, in a, in b, separator, 1f);
        }

        // Divider between ruler and tracks
        ImVec2 rulerA = WidgetDraw.V2(origin.x, origin.y + RulerHeight);
        ImVec2 rulerB = WidgetDraw.V2(origin.x + size.x, origin.y + RulerHeight);
        ImDrawList.AddLine(draw, in rulerA, in rulerB, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 1f);
    }

    private void DrawRuler(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, float height)
    {
        (double step, int minorPerMajor) = ChooseTickStep(size.x);
        uint tickColor = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.45f);

        double first = Math.Floor(ViewStart / step);
        double last = Math.Floor((ViewStart + ViewLength) / step);

        for (double index = first; index <= last; index++)
        {
            double t = index * step;
            float x = XAt(t, origin.x, size.x);
            if (x >= origin.x && x <= origin.x + size.x)
            {
                ImVec2 top = WidgetDraw.V2(x, origin.y + height - 6f);
                ImVec2 bottom = WidgetDraw.V2(x, origin.y + height);
                ImDrawList.AddLine(draw, in top, in bottom, tickColor, 1f);

                string label = FormatPosition(t);
                ImVec2 labelPos = WidgetDraw.V2(x + 3f, origin.y + 2f);
                ImDrawList.AddText(draw, in labelPos, tickColor, label);
            }

            for (int m = 1; m < minorPerMajor; m++)
            {
                float minorX = XAt(t + step * m / minorPerMajor, origin.x, size.x);
                if (minorX < origin.x || minorX > origin.x + size.x) continue;
                ImVec2 minorTop = WidgetDraw.V2(minorX, origin.y + height - 3f);
                ImVec2 minorBottom = WidgetDraw.V2(minorX, origin.y + height);
                ImDrawList.AddLine(draw, in minorTop, in minorBottom, WidgetDraw.WithAlpha(tickColor, 0.6f), 1f);
            }
        }
    }

    private void DrawBeatGrid(ImDrawList* draw, ImVec2 origin, ImVec2 size,
        EditorBeatClock clock, EditorTimeSignatures signatures)
    {
        double startBeat = Math.Max(0, clock.BeatAt(ViewStart));
        double endBeat = clock.BeatAt(ViewStart + ViewLength);
        if (endBeat < startBeat)
            return;

        float top = origin.y + RulerHeight * BeatBandStart;
        float bottom = origin.y + RulerHeight;
        uint bars = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, .54f);
        uint beats = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, .23f);
        uint markers = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrabActive);
        var changes = signatures.Changes;
        double minimumSpacing = Math.Max(0, endBeat - startBeat) * 25 / size.x;
        float nextLabel = origin.x;
        for (int index = signatures.IndexAt(startBeat); index < changes.Count; index++)
        {
            var change = changes[index];
            if (change.Beat > endBeat)
                break;
            double begin = Math.Max(startBeat, change.Beat);
            double end = index + 1 < changes.Count ? Math.Min(endBeat, changes[index + 1].Beat - 1e-8) : endBeat;
            double measureLength = change.Signature.MeasureLength;
            double lead = Math.Ceiling((begin - change.Beat) / measureLength - 1e-7);
            double stride = Math.Max(1, Math.Ceiling(minimumSpacing / measureLength));
            double first = Math.Max(0, Math.Ceiling(lead / stride) * stride);
            if (change.Beat >= startBeat - 1e-7)
                Mark(change.Beat, markers, top, change.Signature.ToString());
            for (double measure = first; change.Beat + measure * measureLength <= end; measure += stride)
            {
                if (measure == 0 && change.Beat >= startBeat - 1e-7)
                    continue;
                Mark(change.Beat + measure * measureLength, bars, top, (change.Measure + measure).ToString("0"));
            }
            double beatLength = change.Signature.BeatLength;
            if (beatLength * 2.5 < minimumSpacing)
                continue;
            double firstBeat = Math.Max(0, Math.Ceiling((begin - change.Beat) / beatLength - 1e-7));
            for (double beat = firstBeat; change.Beat + beat * beatLength <= end; beat++)
                if (beat % change.Signature.Numerator != 0)
                    Mark(change.Beat + beat * beatLength, beats, bottom - 5, null);
        }

        void Mark(double beat, uint color, float y, string? label)
        {
            float x = XAt(clock.TimeAt(beat), origin.x, size.x);
            ImDrawList.AddLine(draw, WidgetDraw.V2(x, y), WidgetDraw.V2(x, bottom), color, 1f);
            if (label is not null && x + 4 >= nextLabel)
            {
                ImDrawList.AddText(draw, WidgetDraw.V2(x + 4, top), color, label);
                nextLabel = x + ImGui.CalcTextSize(label).x + 10;
            }
        }
    }

    private void DrawFloorMarkers(ImDrawList* draw, ImVec2 origin, ImVec2 size,
        ReadOnlySpan<double> floorTimes, int selectedFloor, int? rangeAnchor)
    {
        float top = origin.y + RulerHeight;
        float middle = top + TrackHeight * .5f;
        WidgetDraw.Panel(draw, WidgetDraw.V2(origin.x, top), WidgetDraw.V2(origin.x + size.x, top + TrackHeight),
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_WindowBg, .5f));
        uint normal = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, .64f);
        uint selected = WidgetDraw.U32(.37f, .87f, .81f);
        if (rangeAnchor is int anchor && (uint)anchor < (uint)floorTimes.Length &&
            (uint)selectedFloor < (uint)floorTimes.Length)
        {
            float a = XAt(floorTimes[Math.Min(anchor, selectedFloor)], origin.x, size.x);
            float b = XAt(floorTimes[Math.Max(anchor, selectedFloor)], origin.x, size.x);
            ImDrawList.AddRectFilled(draw, WidgetDraw.V2(a - 4, top), WidgetDraw.V2(b + 4, top + TrackHeight),
                WidgetDraw.WithAlpha(selected, .26f));
        }
        double end = ViewStart + ViewLength;
        double grouping = ViewLength * 6 / size.x;
        float nextLabel = origin.x;
        int first = TimelineFloorTimes.LowerBound(floorTimes, ViewStart);
        for (int index = first; index < floorTimes.Length && floorTimes[index] <= end;)
        {
            double time = floorTimes[index];
            int next = TimelineFloorTimes.LowerBound(floorTimes, time + grouping, index + 1);
            float x = XAt(time, origin.x, size.x);
            bool group = next - index > 1 && floorTimes[index + 1] <= end;
            if (group)
            {
                ImDrawList.AddLine(draw, WidgetDraw.V2(x, middle - 5), WidgetDraw.V2(x, middle + 5), normal, 2f);
            }
            else
            {
                Diamond(x, normal);
                string label = $"#{index}";
                float labelWidth = ImGui.CalcTextSize(label).x;
                double nextTime = next < floorTimes.Length ? Math.Min(floorTimes[next], end) : end;
                if (x >= nextLabel && XAt(nextTime, origin.x, size.x) - x >= labelWidth + 13)
                {
                    ImDrawList.AddText(draw, WidgetDraw.V2(x + 7, middle - ImGui.GetTextLineHeight() * .5f), normal, label);
                    nextLabel = x + labelWidth + 13;
                }
            }
            index = next;
        }
        if ((uint)selectedFloor < (uint)floorTimes.Length && floorTimes[selectedFloor] >= ViewStart &&
            floorTimes[selectedFloor] <= end)
            Diamond(XAt(floorTimes[selectedFloor], origin.x, size.x), selected);

        void Diamond(float x, uint color)
        {
            var left = WidgetDraw.V2(x - 4, middle);
            var right = WidgetDraw.V2(x + 4, middle);
            ImDrawList.AddTriangleFilled(draw, left, WidgetDraw.V2(x, middle - 4), right, color);
            ImDrawList.AddTriangleFilled(draw, left, right, WidgetDraw.V2(x, middle + 4), color);
        }
    }

    private void DrawClips(ImDrawList* draw, in ImVec2 origin, in ImVec2 size, ref bool changed)
    {
        ImVec2 mouse = ImGui.GetMousePos();
        bool leftDown = ImGui.IsMouseDown((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0;
        bool leftClicked = ImGui.IsMouseClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0;

        foreach (TimelineClip clip in _clips)
        {
            float x0 = XAt(clip.Start, origin.x, size.x);
            float x1 = XAt(clip.Start + clip.Duration, origin.x, size.x);

            // Skip clips outside the visible range
            if (x1 < origin.x || x0 > origin.x + size.x)
            {
                continue;
            }

            x0 = MathF.Max(x0, origin.x);
            x1 = MathF.Min(x1, origin.x + size.x);

            float y0 = origin.y + RulerHeight + clip.Track * TrackHeight + 3f;
            float y1 = y0 + TrackHeight - 6f;

            ImVec2 clipMin = WidgetDraw.V2(x0, y0);
            ImVec2 clipMax = WidgetDraw.V2(x1, y1);

            bool isHovered = mouse.x >= x0 && mouse.x <= x1 && mouse.y >= y0 && mouse.y <= y1;

            if (isHovered && leftClicked)
            {
                Selected = clip;
                // Remember where inside the clip the drag started so it does not jump
                double grabTime = TimeAt(mouse.x, origin.x, size.x);
                _drag[clip] = (clip.Start, grabTime - clip.Start);
            }

            bool isDragging = _drag.ContainsKey(clip) && leftDown;

            uint color = WidgetDraw.U32(clip.Color);
            if (clip == Selected)
            {
                color = WidgetDraw.Blend(color, WidgetDraw.U32(1f, 1f, 1f), 0.18f);
            }
            else if (isHovered)
            {
                color = WidgetDraw.Blend(color, WidgetDraw.U32(1f, 1f, 1f), 0.08f);
            }

            WidgetDraw.Panel(draw, in clipMin, in clipMax, color, 3f);

            // Clip the label inside the block
            ImGui.PushClipRect(in clipMin, in clipMax, 1);
            ImVec2 textPos = WidgetDraw.V2(clipMin.x + 5f, clipMin.y + (y1 - y0 - ImGui.GetTextLineHeight()) * 0.5f);
            ImDrawList.AddText(draw, in textPos, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text), clip.Label);
            ImGui.PopClipRect();

            WidgetDraw.Outline(draw, in clipMin, in clipMax, WidgetDraw.WithAlpha(WidgetDraw.U32(1f, 1f, 1f), 0.25f), 3f);

            if (isDragging && _drag.TryGetValue(clip, out (double Start, double GrabOffset) drag))
            {
                double pointer = TimeAt(mouse.x, origin.x, size.x) - drag.GrabOffset;

                if (SnapInterval > 0)
                {
                    pointer = Math.Round(pointer / SnapInterval) * SnapInterval;
                }

                if (Math.Abs(clip.Start - pointer) > 1e-9)
                {
                    clip.Start = pointer;
                    changed = true;
                }
            }

            if (!leftDown)
            {
                _drag.Remove(clip);
            }
        }
    }

    private void DrawPlayhead(ImDrawList* draw, in ImVec2 origin, in ImVec2 size)
    {
        float x = XAt(Playhead, origin.x, size.x);
        if (x < origin.x || x > origin.x + size.x)
        {
            return;
        }

        uint color = WidgetDraw.U32(1f, 0.42f, 0.42f);
        ImVec2 top = WidgetDraw.V2(x, origin.y);
        ImVec2 bottom = WidgetDraw.V2(x, origin.y + size.y);
        ImDrawList.AddLine(draw, in top, in bottom, color, 1.5f);

        // Playhead handle
        ImVec2 a = WidgetDraw.V2(x - 5f, origin.y);
        ImVec2 b = WidgetDraw.V2(x + 5f, origin.y);
        ImVec2 c = WidgetDraw.V2(x, origin.y + 7f);
        ImDrawList.AddTriangleFilled(draw, in a, in b, in c, color);
    }

    private float XAt(double time, float originX, float width)
        => originX + (float)((time - ViewStart) / ViewLength) * width;

    private double TimeAt(float x, float originX, float width)
        => ViewStart + (x - originX) / width * ViewLength;

    private (double Step, int MinorPerMajor) ChooseTickStep(float pixelWidth)
    {
        double widestTime = Math.Max(Math.Abs(ViewStart), Math.Abs(ViewStart + ViewLength));
        float spacing = Math.Max(90, ImGui.CalcTextSize(FormatPosition(-widestTime)).x + 12);
        double minimumStep = ViewLength * spacing / Math.Max(pixelWidth, 1f);
        foreach (double candidate in TickSteps)
        {
            if (candidate >= minimumStep)
                return (candidate, candidate >= 1 ? 5 : candidate >= .002 ? 2 : 1);
        }

        return (Math.Ceiling(minimumStep / TickSteps[^1]) * TickSteps[^1], 5);
    }

    internal static string FormatPosition(double seconds)
    {
        double milliseconds = Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
        string sign = milliseconds < 0 ? "-" : "";
        milliseconds = Math.Abs(milliseconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"{sign}{Math.Floor(milliseconds / 60000):00}:{milliseconds % 60000 / 1000:00.000}");
    }
}

/// <summary>
/// </summary>
public static unsafe class RangeSlider
{
    /// <summary>
    /// Draw a dual-handle range slider.
    /// </summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="min">Allowed minimum.</param>
    /// <param name="max">Allowed maximum.</param>
    /// <param name="low">Current low value.</param>
    /// <param name="high">Current high value.</param>
    /// <param name="format">Value format.</param>
    /// <returns>True if either value changed this frame.</returns>
    public static bool Draw(string id, float min, float max, ref float low, ref float high, string format = "0.##")
    {
        if (max <= min)
        {
            return false;
        }

        float height = ImGui.GetFrameHeight();
        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(avail.x, height);
        ImVec2 origin = ImGui.GetCursorScreenPos();

        ImGui.PushID(id);
        ImGui.InvisibleButton("##range", in size);
        bool hovered = ImGui.IsItemHovered() != 0;
        bool active = ImGui.IsItemActive() != 0;
        ImGui.PopID();

        ImDrawList* draw = ImGui.GetWindowDrawList();
        float trackY = origin.y + height * 0.5f;
        float padding = 8f;
        float trackMin = origin.x + padding;
        float trackMax = origin.x + size.x - padding;

        // Track
        ImVec2 trackA = WidgetDraw.V2(trackMin, trackY - 2f);
        ImVec2 trackB = WidgetDraw.V2(trackMax, trackY + 2f);
        ImDrawList.AddRectFilled(draw, in trackA, in trackB, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 2f);

        low = Math.Clamp(low, min, max);
        high = Math.Clamp(high, low, max);

        float lowX = trackMin + (low - min) / (max - min) * (trackMax - trackMin);
        float highX = trackMin + (high - min) / (max - min) * (trackMax - trackMin);

        // Selected span
        ImVec2 fillA = WidgetDraw.V2(lowX, trackY - 2f);
        ImVec2 fillB = WidgetDraw.V2(highX, trackY + 2f);
        ImDrawList.AddRectFilled(draw, in fillA, in fillB, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrab, 0.85f), 2f);

        bool changed = false;

        // Drag whichever handle is closest to the mouse
        if (hovered && (active || ImGui.IsMouseDown((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0))
        {
            ImVec2 mouse = ImGui.GetMousePos();
            float value = min + Math.Clamp((mouse.x - trackMin) / (trackMax - trackMin), 0f, 1f) * (max - min);

            if (MathF.Abs(mouse.x - lowX) <= MathF.Abs(mouse.x - highX))
            {
                float next = MathF.Min(value, high);
                if (!next.Equals(low))
                {
                    low = next;
                    changed = true;
                }
            }
            else
            {
                float next = MathF.Max(value, low);
                if (!next.Equals(high))
                {
                    high = next;
                    changed = true;
                }
            }
        }

        // Handles
        DrawHandle(draw, lowX, trackY, ImGui.IsMouseDown((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0 && MathF.Abs(ImGui.GetMousePos().x - lowX) < 6f);
        DrawHandle(draw, highX, trackY, ImGui.IsMouseDown((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0 && MathF.Abs(ImGui.GetMousePos().x - highX) < 6f);

        // Value labels
        string text = $"{low.ToString(format)} ~ {high.ToString(format)}";
        ImVec2 labelSize = ImGui.CalcTextSize(text, null, 0, -1f);
        ImVec2 labelPos = WidgetDraw.V2(origin.x + (size.x - labelSize.x) * 0.5f, origin.y - labelSize.y - 2f);

        if (labelPos.y > ImGui.GetWindowPos().y)
        {
            ImDrawList.AddText(draw, in labelPos, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.7f), text);
        }

        if (hovered)
        {
            WidgetDraw.ItemTooltip(text);
        }

        return changed;
    }

    private static void DrawHandle(ImDrawList* draw, float x, float y, bool active)
    {
        float radius = active ? 7f : 6f;
        ImVec2 center = WidgetDraw.V2(x, y);

        uint fill = active
            ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrabActive)
            : WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_SliderGrab);

        ImDrawList.AddCircleFilled(draw, in center, radius, fill, 16);
        ImDrawList.AddCircle(draw, in center, radius, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 16, 1f);
    }
}
