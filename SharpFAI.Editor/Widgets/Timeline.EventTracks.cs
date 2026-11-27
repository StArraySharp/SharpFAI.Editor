using ImGui4NET;

namespace SharpFAI.Editor.Widgets;

public sealed unsafe partial class Timeline
{
    private bool _chartGestureStartedInPlot;
    internal TimelineEventGroup? PickedEventGroup { get; private set; }

    internal static float LaneLabelWidth(ReadOnlySpan<TimelineEventTrack> tracks, float available)
    {
        if (tracks.IsEmpty) return 0;
        float width = ImGui.CalcTextSize(L.T("TimelineTileTrack")).x;
        foreach (var track in tracks)
            width = Math.Max(width, ImGui.CalcTextSize(L.T(track.Label)).x);
        return Math.Min(width + 18, available * .26f);
    }

    private void DrawLaneLabels(ImDrawList* draw, ImVec2 origin, float width, ReadOnlySpan<TimelineEventTrack> tracks)
    {
        float top = origin.y + RulerHeight;
        var end = WidgetDraw.V2(origin.x + width, top + TrackHeight * TrackCount);
        ImDrawList.PushClipRect(draw, origin, end, 1);
        ImDrawList.AddRectFilled(draw, origin, end, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_WindowBg, .62f));
        uint divider = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Border);
        ImDrawList.AddLine(draw, WidgetDraw.V2(end.x, origin.y), end, divider, 1f);
        float textHeight = ImGui.GetTextLineHeight();
        for (int row = 0; row < TrackCount; row++)
        {
            float y = top + row * TrackHeight;
            ImDrawList.AddLine(draw, WidgetDraw.V2(origin.x, y), WidgetDraw.V2(end.x, y), divider, 1f);
            string label = L.T(row == 0 ? "TimelineTileTrack" : tracks[row - 1].Label);
            uint color = row == 0 ? WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, .8f)
                : tracks[row - 1].Color;
            ImDrawList.AddText(draw, WidgetDraw.V2(origin.x + 8, y + (TrackHeight - textHeight) * .5f), color, label);
        }
        ImDrawList.PopClipRect(draw);
    }

    private void DrawEventTracks(ImDrawList* draw, ImVec2 origin, ImVec2 size, ReadOnlySpan<TimelineEventTrack> tracks,
        bool hovered, bool pick, bool tooltip)
    {
        const float groupWidth = 12;
        double groupSeconds = ViewLength * groupWidth / size.x;
        double viewEnd = ViewStart + ViewLength;
        var mouse = ImGui.GetMousePos();
        for (int row = 0; row < tracks.Length; row++)
        {
            var track = tracks[row];
            var events = track.Events;
            float y = origin.y + RulerHeight + (row + 1) * TrackHeight;
            float middle = y + TrackHeight * .5f;
            int limit = TimelineEventTracks.LowerBound(events, Math.BitIncrement(viewEnd));
            bool onRow = hovered && mouse.y >= y && mouse.y < y + TrackHeight;
            float best = 7f;
            TimelineEventGroup? group = null;
            for (int index = TimelineEventTracks.LowerBound(events, ViewStart); index < limit;)
            {
                var marker = events[index];
                int next = Math.Min(limit, TimelineEventTracks.LowerBound(events, marker.Time + groupSeconds, index + 1));
                float x = XAt(marker.Time, origin.x, size.x);
                float distance = MathF.Abs(mouse.x - x);
                if (onRow && distance < best)
                {
                    group = new(track, index, next);
                    best = distance;
                }

                uint color = marker.Enabled || next > index + 1 ? track.Color : WidgetDraw.WithAlpha(track.Color, .36f);
                if (next > index + 1)
                {
                    ImDrawList.AddRectFilled(draw, WidgetDraw.V2(x - 3, middle - 6), WidgetDraw.V2(x + 3, middle + 6), color, 1);
                }
                else
                {
                    ImDrawList.AddQuadFilled(draw, WidgetDraw.V2(x, middle - 4), WidgetDraw.V2(x + 4, middle),
                        WidgetDraw.V2(x, middle + 4), WidgetDraw.V2(x - 4, middle), color);
                    float following = XAt(next < limit ? events[next].Time : viewEnd, origin.x, size.x);
                    if (following - x >= 62)
                    {
                        string text = EditorCatalog.Name(marker.Type);
                        if (ImGui.CalcTextSize(text).x + 14 < following - x)
                            ImDrawList.AddText(draw, WidgetDraw.V2(x + 7, middle - ImGui.GetTextLineHeight() * .5f), color, text);
                    }
                }
                index = next;
            }

            if (group is not { } hit) continue;
            if (pick) PickedEventGroup = hit;
            if (tooltip)
                WidgetDraw.ItemTooltip(hit.Count == 1 ? EventLabel(events[hit.First]) : L.F("TimelineEventGroup", hit.Count));
        }
    }

    internal static string EventLabel(TimelineEventMarker marker) =>
        $"{EditorCatalog.Name(marker.Type)} · {L.F("TimelineFloorAtTime", marker.Floor, FormatPosition(marker.Time))}";
}
