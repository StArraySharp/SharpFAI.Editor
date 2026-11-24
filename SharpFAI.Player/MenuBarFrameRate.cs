using System.Globalization;
using ImGui4NET;

namespace SharpFAI.Player;

internal sealed class MenuBarFrameRate
{
    private double _elapsed;
    private int _frames;
    private string _label = "-- FPS";
    private (string Name, string Suffix, float Width, float FontSize)? _lastClippedStatus;
    private string _clippedStatus = "";

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

    public void Draw(string? status = null, bool showFrameRate = true, string statusSuffix = "")
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
            float right = width - 16f;
            float available = right - ImGui.GetCursorPosX();
            if (available <= 0)
                return;

            string full = status + statusSuffix;
            float fullWidth = TextWidth(full);
            bool clipped = fullWidth > available;
            string label = clipped ? ClipStatus(status, statusSuffix, available) : full;
            if (label.Length == 0)
                return;

            ImGui.SetCursorPosX(right - (clipped ? TextWidth(label) : fullWidth));
            ImGui.TextUnformatted(label);
            if (clipped && ImGui.IsItemHovered() != 0 && ImGui.BeginTooltip() != 0)
            {
                ImGui.TextUnformatted(full);
                ImGui.EndTooltip();
            }
        }
    }

    private string ClipStatus(string status, string suffix, float available)
    {
        var key = (status, suffix, available, ImGui.GetFontSize());
        if (_lastClippedStatus == key)
            return _clippedStatus;
        _lastClippedStatus = key;

        string ellipsis = "…" + suffix;
        if (TextWidth(ellipsis) > available)
            return _clippedStatus = TextWidth(suffix) <= available ? suffix : "";

        // Keep Unicode text elements and the unsaved marker intact when clipping a long file name.
        int[] boundaries = StringInfo.ParseCombiningCharacters(status);
        int first = 0, last = boundaries.Length - 1;
        string best = ellipsis;
        while (first <= last)
        {
            int middle = first + (last - first) / 2;
            string candidate = status[..boundaries[middle]] + ellipsis;
            if (TextWidth(candidate) <= available)
            {
                best = candidate;
                first = middle + 1;
            }
            else
                last = middle - 1;
        }
        return _clippedStatus = best;
    }

    private static float TextWidth(string text) => ImGui.CalcTextSize(text, null, 0, -1f).x;
}
