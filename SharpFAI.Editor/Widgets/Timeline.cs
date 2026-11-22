using ImGui4NET;
using OpenTK.Mathematics;

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
public sealed unsafe class Timeline
{
    private readonly List<TimelineClip> _clips = [];
    private readonly Dictionary<TimelineClip, (double Start, double GrabOffset)> _drag = [];

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
    public bool Draw(string id)
    {
        float height = RulerHeight + TrackHeight * TrackCount;
        ImVec2 avail = ImGui.GetContentRegionAvail();
        ImVec2 size = WidgetDraw.V2(avail.x, height);
        ImVec2 origin = ImGui.GetCursorScreenPos();

        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImVec2 max = WidgetDraw.V2(origin.x + size.x, origin.y + size.y);

        WidgetDraw.Panel(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 4f);

        ImGui.PushID(id);
        ImGui.InvisibleButton("##timeline", in size);
        bool hovered = ImGui.IsItemHovered() != 0;
        ImGui.PopID();

        bool playheadMoved = false;

        // Wheel zooms; holding Ctrl pans instead
        if (hovered && AllowZoom)
        {
            ImGuiIO* io = ImGui.GetIO();
            if (MathF.Abs(io->MouseWheel) > 0.001f)
            {
                if (io->KeyCtrl != 0)
                {
                    ViewStart -= io->MouseWheel * ViewLength * 0.05;
                }
                else
                {
                    double zoom = Math.Exp(-io->MouseWheel * 0.15);
                    double newLength = Math.Clamp(ViewLength * zoom, 0.05, 3600.0);

                    // Zoom around the mouse position
                    ImVec2 mouse = ImGui.GetMousePos();
                    double anchor = ViewStart + Math.Clamp((mouse.x - origin.x) / size.x, 0.0, 1.0) * ViewLength;
                    ViewStart = anchor - (anchor - ViewStart) * (newLength / ViewLength);
                    ViewLength = newLength;
                }
            }
        }

        DrawTracks(draw, in origin, in size);
        DrawRuler(draw, in origin, in size);
        DrawClips(draw, in origin, in size, ref playheadMoved);
        ClipsModified = playheadMoved;

        // Click the ruler to move the playhead
        if (hovered && ImGui.IsMouseClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0)
        {
            ImVec2 mouse = ImGui.GetMousePos();
            if (mouse.y - origin.y <= RulerHeight)
            {
                Playhead = TimeAt(mouse.x, origin.x, size.x);
                playheadMoved = true;
            }
        }

        DrawPlayhead(draw, in origin, in size);

        WidgetDraw.Outline(draw, in origin, in max, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border), 4f);
        return playheadMoved;
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

    private void DrawRuler(ImDrawList* draw, in ImVec2 origin, in ImVec2 size)
    {
        (double step, int minorPerMajor) = ChooseTickStep(size.x);
        uint tickColor = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, 0.45f);

        double first = Math.Floor(ViewStart / step) * step;
        double end = ViewStart + ViewLength;

        for (double t = first; t <= end; t += step)
        {
            float x = XAt(t, origin.x, size.x);
            if (x < origin.x - 1f || x > origin.x + size.x + 1f)
            {
                continue;
            }

            ImVec2 top = WidgetDraw.V2(x, origin.y + RulerHeight - 6f);
            ImVec2 bottom = WidgetDraw.V2(x, origin.y + RulerHeight);
            ImDrawList.AddLine(draw, in top, in bottom, tickColor, 1f);

            string label = FormatTime(t);
            ImVec2 labelPos = WidgetDraw.V2(x + 3f, origin.y + 2f);
            ImDrawList.AddText(draw, in labelPos, tickColor, label);

            // Minor ticks
            if (minorPerMajor > 1)
            {
                for (int m = 1; m < minorPerMajor; m++)
                {
                    double minorTime = t + step * m / minorPerMajor;
                    float minorX = XAt(minorTime, origin.x, size.x);
                    ImVec2 minorTop = WidgetDraw.V2(minorX, origin.y + RulerHeight - 3f);
                    ImVec2 minorBottom = WidgetDraw.V2(minorX, origin.y + RulerHeight);
                    ImDrawList.AddLine(draw, in minorTop, in minorBottom, WidgetDraw.WithAlpha(tickColor, 0.6f), 1f);
                }
            }
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
        // Aim for roughly 90 pixels per major tick
        double raw = ViewLength * 90.0 / Math.Max(pixelWidth, 1f);
        double[] candidates = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

        foreach (double candidate in candidates)
        {
            if (candidate >= raw)
            {
                return (candidate, candidate >= 1 ? 5 : 2);
            }
        }

        return (candidates[^1], 5);
    }

    private static string FormatTime(double seconds)
    {
        if (seconds >= 60)
        {
            int minutes = (int)(seconds / 60);
            double rest = seconds - minutes * 60;
            return $"{minutes}:{rest:00.##}";
        }

        return seconds.ToString("0.##") + "s";
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
