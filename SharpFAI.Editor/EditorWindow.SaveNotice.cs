using System.Diagnostics;
using ImGui4NET;
using SharpFAI.Editor.Widgets;

namespace SharpFAI.Editor;

internal sealed unsafe partial class EditorWindow
{
    private const double SaveNoticeSeconds = 2.5;
    private long? _savedAt;

    private void DrawSaveNotice()
    {
        if (_savedAt is not long timestamp)
            return;
        double timeLeft = SaveNoticeSeconds - Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
        if (timeLeft <= 0)
        {
            _savedAt = null;
            return;
        }

        float alpha = (float)Math.Min(1, timeLeft / .25);
        string message = L.T("ChartSaved");
        ImVec2 text = ImGui.CalcTextSize(message);
        ImGuiViewport* viewport = ImGui.GetMainViewport();
        float margin = 16 * _uiScale;
        float horizontalPadding = 15 * _uiScale;
        float verticalPadding = 10 * _uiScale;
        ImVec2 max = WidgetDraw.V2(viewport->WorkPos.x + viewport->WorkSize.x - margin,
            viewport->WorkPos.y + viewport->WorkSize.y - margin);
        ImVec2 min = WidgetDraw.V2(max.x - text.x - 2 * horizontalPadding,
            max.y - text.y - 2 * verticalPadding);
        ImDrawList* draw = ImGui.GetForegroundDrawList(viewport);
        WidgetDraw.Panel(draw, min, max, WidgetDraw.WithAlpha(WidgetDraw.U32(30, 45, 53, 246), alpha));
        WidgetDraw.Outline(draw, min, max, WidgetDraw.WithAlpha(WidgetDraw.U32(63, 124, 123), alpha));
        ImDrawList.AddText(draw, WidgetDraw.V2(min.x + horizontalPadding, min.y + verticalPadding),
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, alpha), message);
    }
}
