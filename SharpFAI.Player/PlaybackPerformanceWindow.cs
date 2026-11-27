using System.Diagnostics;
using ImGui4NET;
using SharpFAI.Player.Engine;

namespace SharpFAI.Player;

internal sealed unsafe class PlaybackPerformanceWindow(Func<string, string>? localize = null)
{
    private readonly PlaybackPerformance _history = new();
    private object? _source;
    private bool _frozen;
    private int _selected = -1;
    private double _viewStart;
    private double _viewLength;
    private long _lastStats;
    private (float P95, float P99, float Max) _stats;

    public bool IsOpen { get; set; }
    public bool IsCapturing => IsOpen && !_frozen;

    public PlaybackPerformance.WindowFrame BeginFrame(object? source, bool playback)
    {
        if (!ReferenceEquals(source, _source))
        {
            _source = source;
            Clear();
        }
        return _history.BeginFrame(IsCapturing && playback);
    }

    public void Draw(Action<PlaybackPerformance.Sample> seek)
    {
        if (!IsOpen) return;
        bool open = IsOpen;
        ImGui.SetNextWindowSize(new ImVec2 { x = 560, y = 310 }, (int)ImGuiCond.ImGuiCond_FirstUseEver);
        if (ImGui.Begin(Text("PlaybackPerformance", "播放性能") + "###playback-performance", ref open) != 0)
        {
            if (ImGui.Checkbox(Text("PerformanceFreeze", "冻结采样"), ref _frozen) != 0 && !_frozen)
                _selected = -1;
            ImGui.SameLine();
            if (ImGui.Button(Text("PerformanceClear", "清空"), default) != 0)
                Clear();

            if (_history.Count == 0)
                ImGui.TextWrapped(Text("PerformanceEmpty", "开始播放后显示窗口帧间隔。"));
            else
            {
                if (_lastStats == 0 || Stopwatch.GetElapsedTime(_lastStats).TotalSeconds >= .25)
                {
                    _stats = _history.Percentiles();
                    _lastStats = Stopwatch.GetTimestamp();
                }
                ImGui.TextUnformatted(string.Format(Text("PerformanceStats", "窗口帧间隔 · {0:N0} 帧 · P95 {1:0.00} ms · P99 {2:0.00} ms · 最大 {3:0.00} ms"),
                    _history.Count, _stats.P95, _stats.P99, _stats.Max));
                int hovered = DrawGraph(seek);
                int detail = hovered >= 0 ? hovered : _selected >= 0 ? _selected : _history.Count - 1;
                DrawSample(_history[detail]);
            }
        }
        ImGui.End();
        IsOpen = open;
    }

    private int DrawGraph(Action<PlaybackPerformance.Sample> seek)
    {
        var origin = ImGui.GetCursorScreenPos();
        float width = Math.Max(1, ImGui.GetContentRegionAvail().x);
        const float height = 112;
        ImGui.InvisibleButton("##frame-interval", new ImVec2 { x = width, y = height });
        bool hovered = ImGui.IsItemHovered() != 0;
        ImGui.SetItemKeyOwner(ImGuiKey.ImGuiKey_MouseWheelY);
        float mouseFraction = Math.Clamp((ImGui.GetMousePos().x - origin.x) / width, 0, 1);
        int count = _history.Count;
        int endOfHistory = _history.RecordedFrames;
        int startOfHistory = endOfHistory - count;
        double length = _viewLength > 0 ? Math.Min(_viewLength, count) : count;
        double start = Math.Clamp(_viewStart, startOfHistory, endOfHistory - length);
        float wheel = ImGui.GetIO()->MouseWheel;
        if (hovered && wheel != 0)
        {
            double newLength = Math.Clamp(length * Math.Exp(-wheel * .2), 1, count);
            start = Math.Clamp(start + (length - newLength) * mouseFraction, startOfHistory, endOfHistory - newLength);
            length = newLength;
            _viewLength = length < count ? length : 0;
        }
        _viewStart = start;
        int visibleSamples = (int)Math.Round(length);
        int firstSample = Math.Min((int)Math.Round(start - startOfHistory), count - visibleSamples);
        int endSample = firstSample + visibleSamples;

        var draw = ImGui.GetWindowDrawList();
        float bottom = origin.y + height;
        ImDrawList.AddRectFilled(draw, origin, new ImVec2 { x = origin.x + width, y = bottom }, 0xFF222630);

        float max = 12f;
        for (int i = firstSample; i < endSample; i++)
            max = Math.Max(max, _history[i].Interval);
        float targetY = bottom - 10 / max * height;
        ImDrawList.AddLine(draw, new ImVec2 { x = origin.x, y = targetY },
            new ImVec2 { x = origin.x + width, y = targetY }, 0x6689CABA);
        ImDrawList.AddText(draw, new ImVec2 { x = origin.x + 4, y = Math.Max(origin.y, targetY - ImGui.GetTextLineHeight()) },
            0xFF89CABA, "10 ms");

        int columns = Math.Clamp((int)width, 1, visibleSamples);
        int hoverColumn = hovered ? Math.Min((int)(mouseFraction * columns), columns - 1) : -1;
        int highlighted = -1;
        ImVec2 previous = default;
        for (int column = 0; column < columns; column++)
        {
            int first = firstSample + column * visibleSamples / columns;
            int end = firstSample + (column + 1) * visibleSamples / columns;
            int peak = first;
            float interval = _history[first].Interval;
            for (int i = first + 1; i < end; i++)
            {
                float current = _history[i].Interval;
                if (current > interval)
                    (peak, interval) = (i, current);
            }

            var point = new ImVec2 { x = origin.x + (column + .5f) * width / columns,
                y = bottom - interval / max * height };
            if (column > 0)
                ImDrawList.AddLine(draw, previous, point, 0xFFEEB667, 1.5f);
            else if (columns == 1)
                ImDrawList.AddCircleFilled(draw, point, 2, 0xFFEEB667);
            if (column == hoverColumn || _selected >= first && _selected < end)
            {
                ImDrawList.AddLine(draw, new ImVec2 { x = point.x, y = origin.y },
                    new ImVec2 { x = point.x, y = bottom }, 0x80FFFFFF);
                ImDrawList.AddCircleFilled(draw, point, 3, 0xFFFFFFFF);
            }
            if (column == hoverColumn)
                highlighted = peak;
            previous = point;
        }

        if (highlighted >= 0 && ImGui.IsItemClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0)
        {
            _selected = highlighted;
            _frozen = true;
            seek(_history[highlighted]);
        }
        return highlighted;
    }

    private void DrawSample(PlaybackPerformance.Sample sample)
    {
        ImGui.TextWrapped(string.Format(Text("PerformanceSample", "{0} · 砖块 #{1} · {2:0.00} ms · 滚轮缩放 · 点击曲线跳转"),
            TimeSpan.FromSeconds(sample.Time).ToString(@"h\:mm\:ss\.fff"), sample.Floor, sample.Interval));
        var stages = sample.Stages;
        ImGui.TextWrapped(string.Format(Text("PerformanceStages",
            "CPU 用时 (ms)　装饰贴图上传 {0:0.00}　解码等待 {1:0.00}　粒子准备 {2:0.00}　原生滤镜 {3:0.00}"),
            stages.TextureUpload, stages.DecodeWait, stages.Particles, stages.Filters));
    }

    private void Clear()
    {
        _history.Clear();
        _lastStats = 0;
        _selected = -1;
        _viewStart = _viewLength = 0;
    }

    private string Text(string key, string fallback) => localize?.Invoke(key) ?? fallback;
}
