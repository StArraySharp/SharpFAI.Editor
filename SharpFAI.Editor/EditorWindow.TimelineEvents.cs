using ImGui4NET;
using Newtonsoft.Json.Linq;
using SharpFAI.Editor.Widgets;
using SharpFAI.Player.Engine;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private (EditorDocument Document, int Version, LevelRenderer.PlaybackTimeline Timing, double Duration,
        bool ShowTiming, bool ShowEffects)? _timelineEventKey;
    private TimelineEventTrack[] _timelineEventTracks = [];
    private TimelineEventGroup? _timelineEventPicker;

    private int TimelineTrackCount => 1 + TimelineEventTracks.Count(_applicationSettings.ShowTimelineTimingEvents,
        _applicationSettings.ShowTimelineEffectEvents);

    private void UpdateTimelineEventTracks(LevelRenderer.PlaybackTimeline timing, double duration)
    {
        bool showTiming = _applicationSettings.ShowTimelineTimingEvents;
        bool showEffects = _applicationSettings.ShowTimelineEffectEvents;
        var key = (_document, _document.PlaybackVersion, timing, duration, showTiming, showEffects);
        if (_timelineEventKey != key)
        {
            _timelineEventKey = key;
            _timelineEventPicker = null;
            _timelineEventTracks = TimelineEventTracks.Build(_document, timing, showTiming, showEffects, duration);
        }
    }

    private void DrawTimelineEventSelection(LevelRenderer? renderer, bool editing)
    {
        const string popup = "##timeline-event-picker";
        if (renderer is not null && _chartTimeline.PickedEventGroup is { } picked)
        {
            if (picked.Count == 1)
                NavigateToTimelineEvent(renderer, editing, picked.Track.Events[picked.First]);
            else
            {
                _timelineEventPicker = picked;
                ImGui.OpenPopup(popup, 0);
            }
        }
        if (ImGui.BeginPopup(popup, 0) == 0) return;
        if (renderer is not null && _timelineEventPicker is { } group)
        {
            float height = Math.Min(10, group.Count) * ImGui.GetTextLineHeightWithSpacing() + 4;
            if (ImGui.BeginChild("##timeline-event-list", new ImVec2 { x = 340 * _uiScale, y = height }) != 0)
            {
                ImGuiListClipper clipper = default;
                ImGuiListClipper.ImGuiListClipper_Ctor(&clipper);
                try
                {
                    ImGuiListClipper.Begin(&clipper, group.Count, -1);
                    while (ImGuiListClipper.Step(&clipper) != 0)
                    {
                        for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                        {
                            var marker = group.Track.Events[group.First + row];
                            ImGui.PushID(row);
                            if (ImGui.Selectable(Timeline.EventLabel(marker), 0, 0, default) != 0)
                            {
                                NavigateToTimelineEvent(renderer, editing, marker);
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.PopID();
                        }
                    }
                }
                finally { ImGuiListClipper.Dispose(&clipper); }
            }
            ImGui.EndChild();
        }
        else ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void NavigateToTimelineEvent(LevelRenderer renderer, bool editing, TimelineEventMarker marker)
    {
        SeekTimeline(renderer, marker.Time, editing, -1);
        _chartTimeline.Playhead = marker.Time;
        if (!editing) return;

        SelectFloor(marker.Floor, false);
        _playbackFloor = null;
        _showInspector = true;
        if (marker.Source is JObject decoration && ReferenceEquals(decoration.Parent, _document.Decorations))
        {
            SelectDecoration(decoration);
            return;
        }
        var items = _document.Events(marker.Floor);
        var selected = marker.Source as JObject ?? marker.Source.Annotation<JObject>();
        _eventType = marker.Type;
        _eventPage = 0;
        foreach (var item in items)
        {
            if (ReferenceEquals(item, selected)) break;
            if (EditorDocument.Type(item) == marker.Type) _eventPage++;
        }
    }
}
