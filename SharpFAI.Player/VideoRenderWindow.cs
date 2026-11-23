using ImGui4NET;
using SharpFAI.Serialization;
using static SharpFAI.Player.VideoExportSession;

namespace SharpFAI.Player;

internal sealed unsafe class VideoRenderWindow(Func<string, string>? localize = null)
{
    private bool _open;
    private bool _focusOnOpen;
    private int _width = 1920;
    private int _height = 1080;
    private int _fps = 60;
    private bool _preferHardware = true;

    public void Open()
    {
        _open = true;
        _focusOnOpen = true;
    }

    public readonly record struct Settings(int Width, int Height, int Fps, bool PreferHardware)
    {
        public VideoExporter.Request CreateRequest(string levelPath, Level level) => new(
            levelPath, VideoExporter.ResolveOutputPath(levelPath, level), Width, Height, Fps,
            KeepFrames: false, BitrateKbps: 0, Zoom: 0f, HidePassedFloors: false, PreferHardware: PreferHardware);
    }

    public void Draw(VideoExportStatus status, Action<Settings> start, Action cancel, string? unavailable = null)
    {
        if (!_open)
            return;

        ImGuiViewport* viewport = ImGui.GetMainViewport();
        ImVec2 center = new()
        {
            x = viewport->WorkPos.x + viewport->WorkSize.x * .5f,
            y = viewport->WorkPos.y + viewport->WorkSize.y * .5f,
        };
        ImGui.SetNextWindowPos(center, (int)ImGuiCond.ImGuiCond_FirstUseEver, new ImVec2 { x = .5f, y = .5f });
        ImGui.SetNextWindowSize(new ImVec2 { x = Math.Clamp(viewport->WorkSize.x - 16f, 1f, 360f) },
            (int)ImGuiCond.ImGuiCond_Always);
        if (_focusOnOpen)
        {
            ImGui.SetNextWindowFocus();
            _focusOnOpen = false;
        }

        bool open = _open;
        int flags = (int)(ImGuiWindowFlags.ImGuiWindowFlags_AlwaysAutoResize | ImGuiWindowFlags.ImGuiWindowFlags_NoCollapse);
        if (ImGui.Begin(Text("VideoRenderTitle", "视频渲染") + "###video-render", ref open, flags) != 0)
        {
            if (status.Active)
                DrawProgress(status, cancel);
            else
                DrawSettings(status, start, unavailable);
        }
        ImGui.End();
        _open = open;
    }

    private string Text(string key, string fallback) => localize?.Invoke(key) ?? fallback;

    private void DrawProgress(VideoExportStatus status, Action cancel)
    {
        ImGui.TextUnformatted(status.FrameCount == 0
            ? Text("VideoRenderPreparing", "正在准备渲染…")
            : string.Format(Text("VideoRenderProgress", "渲染中  {0:N0} / {1:N0} 帧"), status.Frame, status.FrameCount));
        ImGui.ProgressBar((float)status.Progress, new ImVec2 { x = -1f }, $"{status.Progress:P1}");
        if (status.Frame > 0)
            ImGui.TextUnformatted(string.Format(Text("VideoRenderRate", "渲染速度  {0:0.0} 帧/秒"), status.FramesPerSecond));

        double eta = status.EtaSeconds;
        if (eta > 0.0)
            ImGui.TextUnformatted(string.Format(Text("VideoRenderEta", "预计剩余 {0}:{1:00}"), (int)(eta / 60.0), eta % 60));

        if (ImGui.Button(Text("VideoRenderCancel", "取消渲染"), default) != 0)
            cancel();
    }

    private void DrawSettings(VideoExportStatus status, Action<Settings> start, string? unavailable)
    {
        DrawSetting(Text("VideoRenderWidth", "宽度 (px)"), ref _width, 16, 7680);
        DrawSetting(Text("VideoRenderHeight", "高度 (px)"), ref _height, 16, 4320);
        DrawSetting(Text("VideoRenderFps", "帧率 (FPS)"), ref _fps, 1, 480);

        if (ImGui.Button("720p60", default) != 0)
            (_width, _height, _fps) = (1280, 720, 60);
        ImGui.SameLine();
        if (ImGui.Button("1080p60", default) != 0)
            (_width, _height, _fps) = (1920, 1080, 60);
        ImGui.SameLine();
        if (ImGui.Button("4K60", default) != 0)
            (_width, _height, _fps) = (3840, 2160, 60);

        bool preferHardware = _preferHardware;
        if (ImGui.Checkbox(Text("VideoRenderHardware", "优先使用硬件编码"), ref preferHardware) != 0)
            _preferHardware = preferHardware;

        ImGui.Separator();
        ImGui.BeginDisabled((byte)(unavailable is not null ? 1 : 0));
        if (ImGui.Button(Text("VideoRenderStart", "开始渲染"), new ImVec2 { x = -1f }) != 0)
            start(new Settings(_width, _height, _fps, _preferHardware));
        ImGui.EndDisabled();

        ImGui.PushTextWrapPos(0f);
        if (unavailable is not null)
            ImGui.TextUnformatted(unavailable);
        if (status.Cancelled)
            ImGui.TextUnformatted(Text("VideoRenderCancelled", "已取消渲染"));
        else if (status.Error is { Length: > 0 } error)
            ImGui.TextUnformatted(error);
        else if (status.OutputPath is { Length: > 0 } output)
            ImGui.TextUnformatted(string.Format(Text("VideoRenderOutput", "已输出  {0}"), output));
        ImGui.PopTextWrapPos();
    }

    private static void DrawSetting(string label, ref int value, int min, int max)
    {
        int input = value;
        ImGui.SetNextItemWidth(-100f);
        if (ImGui.InputInt(label, ref input, 0, 0) != 0)
            value = Math.Clamp(input, min, max);
    }
}
