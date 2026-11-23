using ImGui4NET;

namespace SharpFAI.Player;

internal sealed class MenuBarFrameRate
{
    private double _elapsed;
    private int _frames;
    private string _label = "-- FPS";

    public void Sample(double frameSeconds)
    {
        if (frameSeconds <= 0 || !double.IsFinite(frameSeconds))
            return;

        _elapsed += frameSeconds;
        _frames++;
        if (_elapsed < 1.0)
            return;

        _label = $"{_frames / _elapsed:0} FPS";
        _elapsed = 0;
        _frames = 0;
    }

    public void Draw(string? status = null, bool showFrameRate = true)
    {
        if (!showFrameRate && status is null)
            return;

        float width = ImGui.GetWindowWidth();
        ImGui.SameLine();
        if (showFrameRate)
        {
            float labelWidth = ImGui.CalcTextSize(_label, null, 0, -1f).x;
            float left = (width - labelWidth) * 0.5f;
            if (left >= ImGui.GetCursorPosX())
            {
                ImGui.SetCursorPosX(left);
                ImGui.TextUnformatted(_label);
                if (status is not null)
                    ImGui.SameLine();
            }
        }

        if (status is not null)
        {
            float statusWidth = ImGui.CalcTextSize(status, null, 0, -1f).x;
            ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), width - statusWidth - 16f));
            ImGui.TextUnformatted(status);
        }
    }
}
