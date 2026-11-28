using ImGui4NET;

namespace SharpFAI.Editor.Widgets;

public sealed unsafe partial class Timeline
{
    private TimelineWaveform? _waveform;
    private TimelineWaveform.Peak[] _waveformColumns = [];
    private (double Start, double Length, int Columns)? _waveformViewport;

    internal TimelineWaveform? Waveform
    {
        get => _waveform;
        set
        {
            if (ReferenceEquals(_waveform, value)) return;
            _waveform = value;
            _waveformViewport = null;
        }
    }

    private void DrawWaveform(ImDrawList* draw, ImVec2 origin, float width)
    {
        if (Waveform is not { } waveform) return;

        int columns = Math.Max(1, (int)MathF.Ceiling(width));
        var view = (ViewStart, ViewLength, columns);
        if (_waveformViewport != view)
        {
            if (_waveformColumns.Length < columns)
                _waveformColumns = new TimelineWaveform.Peak[columns];
            double secondsPerColumn = ViewLength / columns;
            for (int i = 0; i < columns; i++)
                _waveformColumns[i] = waveform.Measure(ViewStart + i * secondsPerColumn, ViewStart + (i + 1) * secondsPerColumn);
            _waveformViewport = view;
        }

        // Use the beat band of the ruler so the range can be dragged directly over the music.
        float halfHeight = (RulerHeight - BeatBandOffset) * .5f;
        float middle = origin.y + RulerHeight - halfHeight;
        float amplitude = Math.Max(0, halfHeight - 2);
        ImDrawList.AddLine(draw, WidgetDraw.V2(origin.x, middle), WidgetDraw.V2(origin.x + width, middle),
            WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_PlotLines, .18f));
        uint color = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_PlotLines, .60f);
        float pixelWidth = width / columns;
        float scale = amplitude / 32768f;
        for (int i = 0; i < columns; i++)
        {
            var peak = _waveformColumns[i];
            if (peak.Min > peak.Max || peak is { Min: 0, Max: 0 }) continue;
            float left = origin.x + i * pixelWidth;
            float right = origin.x + (i + 1) * pixelWidth;
            float upper = middle - peak.Max * scale;
            float lower = middle - peak.Min * scale;
            ImDrawList.AddRectFilled(draw, WidgetDraw.V2(left, upper),
                WidgetDraw.V2(right, Math.Max(upper + 1, lower)), color);
        }
    }
}
