using ImGui4NET;

namespace SharpFAI.Player;

internal sealed unsafe class VideoRenderWindow
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

    public void Draw(PlayerWindow player, bool levelReady)
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
        if (ImGui.Begin("视频渲染", ref open, flags) != 0)
        {
            PlayerWindow.VideoExportStatus status = player.ExportStatus;
            if (status.Active)
                DrawProgress(player, status);
            else
                DrawSettings(player, levelReady, status);
        }
        ImGui.End();
        _open = open;
    }

    private static void DrawProgress(PlayerWindow player, PlayerWindow.VideoExportStatus status)
    {
        ImGui.TextUnformatted(status.FrameCount == 0
            ? "正在准备渲染…"
            : $"渲染中  {status.Frame:N0} / {status.FrameCount:N0} 帧");
        ImGui.ProgressBar((float)status.Progress, new ImVec2 { x = -1f }, $"{status.Progress:P1}");
        if (status.Frame > 0)
            ImGui.TextUnformatted($"渲染速度  {status.FramesPerSecond:0.0} 帧/秒");

        double eta = status.EtaSeconds;
        if (eta > 0.0)
            ImGui.TextUnformatted($"预计剩余 {(int)(eta / 60.0)}:{eta % 60:00}");

        if (ImGui.Button("取消渲染", default) != 0)
            player.CancelVideoExport();
    }

    private void DrawSettings(PlayerWindow player, bool levelReady, PlayerWindow.VideoExportStatus status)
    {
        DrawSetting("宽度 (px)", ref _width, 16, 7680);
        DrawSetting("高度 (px)", ref _height, 16, 4320);
        DrawSetting("帧率 (FPS)", ref _fps, 1, 480);

        if (ImGui.Button("720p60", default) != 0)
            (_width, _height, _fps) = (1280, 720, 60);
        ImGui.SameLine();
        if (ImGui.Button("1080p60", default) != 0)
            (_width, _height, _fps) = (1920, 1080, 60);
        ImGui.SameLine();
        if (ImGui.Button("4K60", default) != 0)
            (_width, _height, _fps) = (3840, 2160, 60);

        bool preferHardware = _preferHardware;
        if (ImGui.Checkbox("优先使用硬件编码", ref preferHardware) != 0)
            _preferHardware = preferHardware;

        ImGui.Separator();
        if (!levelReady)
            ImGui.BeginDisabled();
        if (ImGui.Button("开始渲染", new ImVec2 { x = -1f }) != 0)
            player.RequestVideoExport(_width, _height, _fps, 0.5f, _preferHardware);
        if (!levelReady)
        {
            ImGui.TextUnformatted("请先加载关卡");
            ImGui.EndDisabled();
        }

        ImGui.PushTextWrapPos(0f);
        if (status.Error is { Length: > 0 } error)
            ImGui.TextUnformatted(error);
        else if (status.OutputPath is { Length: > 0 } output)
            ImGui.TextUnformatted($"已输出  {output}");
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
