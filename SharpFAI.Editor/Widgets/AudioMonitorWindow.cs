using System.Diagnostics;
using System.Globalization;
using ImGui4NET;
using Thrust.Audio;

namespace SharpFAI.Editor.Widgets;

internal sealed unsafe class AudioMonitorWindow
{
    private const int MaxPeaks = 10;
    private static readonly int[] FrequencyTicks = [AudioMonitorAnalysis.MinFrequency, 100, 500, 2000, 10000];
    private AudioMonitorAnalysis? _analysis;
    private DecodedAudio? _source;
    private AudioSampleWindow? _window;
    private bool _readFailed;
    private long _lastSample;
    private double _position;
    private bool _wasPlaying;
    private int _peakCount = 5;

    internal bool IsOpen;

    internal void Draw(DecodedAudio? music, double seconds, bool playing)
    {
        if (!IsOpen)
        {
            ClearSource();
            return;
        }

        if (music is not { Channels: 1 or 2, SampleRate: > AudioMonitorAnalysis.MinFrequency * 2, FrameCount: > 0 })
            music = null;
        if (!ReferenceEquals(music, _source))
        {
            ClearSource();
            _source = music;
            _lastSample = 0;
        }

        ImGui.SetNextWindowSize(WidgetDraw.V2(560, 320), (int)ImGuiCond.ImGuiCond_FirstUseEver);
        if (ImGui.Begin(L.T("AudioMonitor") + "###audio-monitor", ref IsOpen) != 0)
        {
            if (music is null || _readFailed)
                ImGui.TextWrapped(L.T("AudioMonitorNoMusic"));
            else
                DrawContent(music, seconds, playing);
        }
        ImGui.End();
    }

    internal void ClearSource()
    {
        _window?.Dispose();
        _window = null;
        _readFailed = false;
        _source = null;
        _lastSample = 0;
    }

    private void DrawContent(DecodedAudio music, double seconds, bool playing)
    {
        var analysis = _analysis ??= new();
        double elapsed = Stopwatch.GetElapsedTime(_lastSample).TotalSeconds;
        if (_lastSample == 0 || playing != _wasPlaying ||
            seconds != _position && (!playing || elapsed >= .04 || Math.Abs(seconds - _position) > .2))
        {
            try { analysis.Analyze(music, _window ??= new(music), seconds); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _window?.Dispose();
                _window = null;
                _readFailed = true;
                Console.Error.WriteLine($"[Audio] Monitor: {error.Message}");
                return;
            }
            _position = seconds;
            _lastSample = Stopwatch.GetTimestamp();
            _wasPlaying = playing;
        }

        ImGui.SetNextItemWidth(Math.Min(ImGui.GetFontSize() * 8, ImGui.GetContentRegionAvail().x * .45f));
        ImGui.SliderInt(L.T("AudioMonitorPeakCount") + "###audio-peak-count", ref _peakCount, 1, MaxPeaks, "%d");
        _peakCount = Math.Clamp(_peakCount, 1, MaxPeaks);
        string position = Timeline.FormatPosition(_position);
        if (ImGui.GetContentRegionAvail().x >= ImGui.GetItemRectSize().x + ImGui.CalcTextSize(position).x + 24)
            ImGui.SameLine();
        WidgetDraw.TextDisabled(position);

        ImVec2 size = ImGui.GetContentRegionAvail();
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        if (size.x >= lineHeight * 6)
            DrawSpectrum(analysis, music.SampleRate, size.x, size.y, lineHeight);
    }

    private void DrawSpectrum(AudioMonitorAnalysis analysis, int sampleRate, float width, float height, float lineHeight)
    {
        float highest = sampleRate / 2f;
        float labelWidth = ImGui.CalcTextSize(FrequencyLabel(highest)).x + 8;
        int labelsPerRow = Math.Max(1, (int)((width - 16) / labelWidth));
        int labelRows = (_peakCount + labelsPerRow - 1) / labelsPerRow;
        var draw = ImGui.GetWindowDrawList();
        height = Math.Max(height, (6 + labelRows) * lineHeight);
        var origin = ImGui.GetCursorScreenPos();
        var end = WidgetDraw.V2(origin.x + width, origin.y + height);
        ImGui.Dummy(WidgetDraw.V2(width, height));
        ImDrawList.PushClipRect(draw, origin, end, 1);
        WidgetDraw.Panel(draw, origin, end, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg, .65f), 3);
        float left = origin.x + 8;
        float right = end.x - 8;
        float labelTop = origin.y + 4;
        float top = labelTop + (1 + labelRows) * lineHeight;
        float bottom = end.y - lineHeight;
        float ratio = highest / AudioMonitorAnalysis.MinFrequency;
        float logRange = MathF.Log(ratio);
        float X(float frequency) => left + MathF.Log(frequency / AudioMonitorAnalysis.MinFrequency) / logRange * (right - left);
        float Y(float level) => bottom - (1 - Math.Clamp(level / AudioMonitorAnalysis.MinimumLevel, 0, 1)) * (bottom - top);
        uint grid = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Separator, .55f);
        uint fill = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_PlotHistogram, .75f);
        uint dim = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_TextDisabled);

        float nextLabel = left;
        foreach (int tick in FrequencyTicks)
        {
            if (tick >= highest) break;
            float x = X(tick);
            ImDrawList.AddLine(draw, WidgetDraw.V2(x, top), WidgetDraw.V2(x, bottom), grid);
            string label = tick >= 1000 ? $"{tick / 1000}k" : tick.ToString(CultureInfo.InvariantCulture);
            float tickWidth = ImGui.CalcTextSize(label).x;
            if (x < nextLabel || x + tickWidth + 28 > right) continue;
            ImDrawList.AddText(draw, WidgetDraw.V2(x, bottom + 2), dim, label);
            nextLabel = x + tickWidth + 8;
        }
        ImDrawList.AddText(draw, WidgetDraw.V2(right - ImGui.CalcTextSize("Hz").x, bottom + 2), dim, "Hz");
        for (int level = 0; level >= AudioMonitorAnalysis.MinimumLevel; level -= 30)
        {
            float y = Y(level);
            ImDrawList.AddLine(draw, WidgetDraw.V2(left, y), WidgetDraw.V2(right, y), grid);
        }

        var levels = analysis.Levels;
        float binsPerHz = (float)AudioMonitorAnalysis.SampleCount / sampleRate;
        int columns = Math.Clamp((int)(right - left), 1, 640);
        float step = (right - left) / columns;
        float thickness = Math.Max(1, step);
        float previousBin = AudioMonitorAnalysis.MinFrequency * binsPerHz;
        for (int column = 0; column < columns; column++)
        {
            float endBin = AudioMonitorAnalysis.MinFrequency * MathF.Pow(ratio, (column + 1f) / columns) * binsPerHz;
            float middle = (previousBin + endBin) * .5f;
            int nearest = Math.Clamp((int)middle, 0, levels.Length - 2);
            float level = levels[nearest] + (levels[nearest + 1] - levels[nearest]) * (middle - nearest);
            for (int bin = (int)MathF.Ceiling(previousBin); bin <= (int)endBin && bin < levels.Length; bin++)
                level = Math.Max(level, levels[bin]);
            previousBin = endBin;
            if (level <= AudioMonitorAnalysis.MinimumLevel) continue;
            float x = left + (column + .5f) * step;
            ImDrawList.AddLine(draw, WidgetDraw.V2(x, Y(level)), WidgetDraw.V2(x, bottom), fill, thickness);
        }

        Span<LabelBounds> labels = stackalloc LabelBounds[MaxPeaks];
        int placed = 0;
        for (int i = 0; i < Math.Min(_peakCount, analysis.Peaks.Count); i++)
        {
            var peak = analysis.Peaks[i];
            var point = WidgetDraw.V2(X(peak.Frequency), Y(peak.Level));
            if (DrawPeakLabel(draw, point, FrequencyLabel(peak.Frequency), left, right, labelTop, labels[..placed], out var bounds))
                labels[placed++] = bounds;
        }
        ImDrawList.PopClipRect(draw);
    }

    private static string FrequencyLabel(float frequency) =>
        string.Create(CultureInfo.InvariantCulture, $"{frequency:0.0} Hz");

    private static bool DrawPeakLabel(ImDrawList* draw, ImVec2 point, string label, float left, float right,
        float top, ReadOnlySpan<LabelBounds> occupied, out LabelBounds bounds)
    {
        const float padding = 3;
        var size = ImGui.CalcTextSize(label);
        float width = size.x + 2 * padding;
        float height = size.y + 2;
        uint text = WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text);
        ImDrawList.AddCircleFilled(draw, point, 2, text);
        bounds = default;
        if (width > right - left) return false;
        float preferred = Math.Clamp(point.x - width / 2, left, right - width);
        float y = point.y - height - 5;
        while (y >= top)
        {
            float bestX = 0;
            float distance = float.PositiveInfinity;
            for (int i = -1; i < occupied.Length; i++)
            {
                // The preferred position or the edge of an existing label can start a free interval.
                for (int side = 0; side < (i < 0 ? 1 : 2); side++)
                {
                    float x = i < 0 ? preferred : side == 0 ? occupied[i].Right + 2 : occupied[i].Left - width - 2;
                    if (x < left || x + width > right || Math.Abs(x - preferred) >= distance) continue;
                    var candidate = new LabelBounds(x, y, x + width, y + height);
                    if (Overlaps(candidate, occupied)) continue;
                    bestX = x;
                    distance = Math.Abs(x - preferred);
                }
            }
            if (float.IsFinite(distance))
            {
                bounds = new(bestX, y, bestX + width, y + height);
                var tip = WidgetDraw.V2(Math.Clamp(point.x, bounds.Left, bounds.Right), bounds.Bottom);
                ImDrawList.AddLine(draw, point, tip, WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_Text, .65f));
                WidgetDraw.Panel(draw, WidgetDraw.V2(bounds.Left, bounds.Top), WidgetDraw.V2(bounds.Right, bounds.Bottom),
                    WidgetDraw.ThemeColor(ImGuiCol.ImGuiCol_FrameBg), 2);
                ImDrawList.AddText(draw, WidgetDraw.V2(bestX + padding, y + 1), text, label);
                return true;
            }
            y -= height + 2;
        }
        return false;
    }

    private static bool Overlaps(LabelBounds candidate, ReadOnlySpan<LabelBounds> occupied)
    {
        foreach (var other in occupied)
            if (candidate.Left < other.Right + 2 && other.Left < candidate.Right + 2 &&
                candidate.Top < other.Bottom + 2 && other.Top < candidate.Bottom + 2)
                return true;
        return false;
    }

    private readonly record struct LabelBounds(float Left, float Top, float Right, float Bottom);
}
