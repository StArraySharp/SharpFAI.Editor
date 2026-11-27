using ImGui4NET;

namespace SharpFAI.Editor.Widgets;

public sealed unsafe partial class Timeline
{
    private static readonly uint WaveformColor = WidgetDraw.U32(127, 174, 188);
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
        float halfHeight = RulerHeight * (1 - BeatBandStart) * .5f;
        float middle = origin.y + RulerHeight - halfHeight;
        float amplitude = Math.Max(0, halfHeight - 2);
        ImDrawList.AddLine(draw, WidgetDraw.V2(origin.x, middle), WidgetDraw.V2(origin.x + width, middle),
            WidgetDraw.WithAlpha(WaveformColor, .18f));
        uint color = WidgetDraw.WithAlpha(WaveformColor, .6f);
        float pixelWidth = width / columns;
        for (int i = 0; i < columns; i++)
        {
            var peak = _waveformColumns[i];
            if (peak.Min > peak.Max || peak is { Min: 0, Max: 0 }) continue;
            float x = origin.x + (i + .5f) * pixelWidth;
            float upper = middle - peak.Max * (amplitude / 32768f);
            float lower = middle - peak.Min * (amplitude / 32768f);
            ImDrawList.AddLine(draw, WidgetDraw.V2(x, upper), WidgetDraw.V2(x, Math.Max(upper + 1, lower)), color);
        }
    }
}
