using ImGui4NET;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Play;

namespace SharpFAI.Player;

internal sealed unsafe class LevelPanel
{
    private const float PanelWidth = 312f;
    private const double LastHitDisplaySeconds = 0.9;
    private const int FixedWindowFlags = (int)(ImGuiWindowFlags.ImGuiWindowFlags_NoTitleBar
        | ImGuiWindowFlags.ImGuiWindowFlags_NoResize | ImGuiWindowFlags.ImGuiWindowFlags_NoMove
        | ImGuiWindowFlags.ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags.ImGuiWindowFlags_NoSavedSettings);

    private bool _expanded;
    private double _fpsSampleAt;
    private double _fpsDisplay;
    private double _lastHitSeenAt = double.NegativeInfinity;
    private HitRecord? _lastHitShown;

    public void Draw(LevelRenderer renderer, float top, bool rendering, AudioCalibrationWindow calibration)
    {
        if (!_expanded || rendering || renderer.IsLoading || renderer.Level is null)
            renderer.SetProgressScrubbing(false);
        ImGuiViewport* viewport = ImGui.GetMainViewport();
        float left = viewport->Pos.x;
        float tabSize = ImGui.GetFrameHeight() + 2f;
        float width = Math.Clamp(viewport->Size.x - tabSize, 1f, PanelWidth);
        if (_expanded)
        {
            ImGui.SetNextWindowPos(new ImVec2 { x = left, y = top }, (int)ImGuiCond.ImGuiCond_Always, default);
            ImGui.SetNextWindowSize(new ImVec2
            {
                x = width,
                y = Math.Max(1f, viewport->Pos.y + viewport->Size.y - top),
            }, (int)ImGuiCond.ImGuiCond_Always);

            if (ImGui.Begin("关卡##level", FixedWindowFlags) != 0)
            {
                if (rendering)
                    ImGui.BeginDisabled();
                DrawContents(renderer, calibration);
                if (rendering)
                    ImGui.EndDisabled();
            }
            ImGui.End();
        }

        ImGui.SetNextWindowPos(new ImVec2 { x = _expanded ? left + width : left, y = top + 8f },
            (int)ImGuiCond.ImGuiCond_Always, default);
        ImGui.SetNextWindowSize(new ImVec2 { x = tabSize, y = tabSize }, (int)ImGuiCond.ImGuiCond_Always);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowPadding, new ImVec2());
        int flags = FixedWindowFlags | (int)(ImGuiWindowFlags.ImGuiWindowFlags_NoBackground
            | ImGuiWindowFlags.ImGuiWindowFlags_AlwaysAutoResize);
        if (ImGui.Begin("##level-tab", flags) != 0)
        {
            bool expanded = _expanded;
            ImGuiDir direction = expanded ? ImGuiDir.ImGuiDir_Left : ImGuiDir.ImGuiDir_Right;
            if (ImGui.ArrowButton("##level-toggle", direction) != 0)
                _expanded = !expanded;
            if (ImGui.BeginItemTooltip() != 0)
            {
                ImGui.TextUnformatted(expanded ? "收起关卡面板" : "展开关卡面板");
                ImGui.EndTooltip();
            }
        }
        ImGui.End();
        ImGui.PopStyleVar(1);
    }

    private void DrawContents(LevelRenderer renderer, AudioCalibrationWindow calibration)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("关卡");
        ImGui.SameLine();
        bool canReload = renderer.LevelPath is not null && !renderer.IsLoading;
        if (!canReload)
            ImGui.BeginDisabled();
        if (ImGui.Button("重新加载", default) != 0)
            renderer.ReloadLevel();
        if (!canReload)
            ImGui.EndDisabled();

        if (renderer.IsLoading)
        {
            ImGui.TextUnformatted($"正在加载 {renderer.LoadPhase} · {FormatTime(renderer.LoadElapsed)}");
            return;
        }

        if (renderer.Level is null)
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(renderer.Status);
            ImGui.PopTextWrapPos();
            return;
        }

        if (renderer.LoadTotalMilliseconds > 0)
            ImGui.TextUnformatted($"加载完成 · 共 {renderer.LoadTotalMilliseconds / 1000:F2} 秒");

        DrawPlayback(renderer);
        DrawManualPlay(renderer);
        DrawAudio(renderer, calibration);
        DrawPerformance(renderer);
    }

    private static void DrawPlayback(LevelRenderer renderer)
    {
        ImGui.SeparatorText("播放");
        float width = ImGui.GetContentRegionAvail().x;
        float gap = ImGui.GetStyle()->ItemSpacing.x;
        ImVec2 third = new() { x = MathF.Max(1f, (width - 2f * gap) / 3f) };
        if (ImGui.Button(renderer.IsPlaying ? "暂停" : "播放", third) != 0)
            renderer.TogglePlay();

        ImGui.SameLine();
        if (ImGui.Button("回到开头", third) != 0)
            renderer.Restart();

        ImGui.SameLine();
        if (ImGui.Button("跳到结尾", third) != 0)
        {
            renderer.Pause();
            renderer.SelectFloor(renderer.FloorCount - 1);
        }

        ImVec2 button = new() { x = MathF.Max(1f, (width - gap) * .5f) };
        bool canUseSelection = renderer.SelectedFloor >= 0 && !renderer.IsPlaying;
        if (!canUseSelection)
            ImGui.BeginDisabled();
        if (ImGui.Button("从选中播放", button) != 0)
        {
            renderer.SelectFloor(renderer.SelectedFloor);
            renderer.Play();
        }
        ImGui.SameLine();
        if (ImGui.Button("清除选择", button) != 0)
            renderer.SelectFloor(-1);
        if (!canUseSelection)
            ImGui.EndDisabled();

        if (renderer.SelectedFloor >= 0)
            ImGui.TextUnformatted($"已选中砖块 #{renderer.SelectedFloor:N0}");

        float progress = (float)renderer.Progress;
        ImGui.SetNextItemWidth(-1f);
        bool changed = ImGui.SliderFloat("##progress", ref progress, 0f, 1f, "", 0) != 0;
        renderer.SetProgressScrubbing(ImGui.IsItemActive() != 0 &&
            ImGui.IsMouseDown((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0);
        if (changed)
            renderer.Seek(progress * renderer.Duration);
        string time = $"{FormatTime(renderer.PlayTime)} / {FormatTime(renderer.Duration)}";
        ImVec2 min = ImGui.GetItemRectMin();
        ImVec2 max = ImGui.GetItemRectMax();
        ImVec2 size = ImGui.CalcTextSize(time);
        ImDrawList* draw = ImGui.GetWindowDrawList();
        ImDrawList.PushClipRect(draw, min, max, 1);
        ImDrawList.AddText(draw, new ImVec2 { x = (min.x + max.x - size.x) * .5f, y = (min.y + max.y - size.y) * .5f },
            ImGui.GetColorU32((int)ImGuiCol.ImGuiCol_Text, 1f), time);
        ImDrawList.PopClipRect(draw);
    }

    private void DrawManualPlay(LevelRenderer renderer)
    {
        ImGui.SeparatorText("手打");
        bool manual = renderer.ManualPlayEnabled;
        if (ImGui.Checkbox("手打模式 (M)", ref manual) != 0)
            renderer.SetManualPlay(manual);

        ImGui.SameLine();
        bool noFail = renderer.PlaySession.NoFail;
        if (ImGui.Checkbox("不死模式", ref noFail) != 0)
            renderer.PlaySession.NoFail = noFail;
        if (!manual)
            return;

        if (ImGui.Button("重新开始", default) != 0)
            renderer.ResetManualPlay();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("判定窗口");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        float margin = (float)renderer.PlayInput.MarginMultiplier;
        if (ImGui.SliderFloat("##marginMult", ref margin, 0.5f, 2.0f, "%.2f×", 0) != 0)
            renderer.PlayInput.MarginMultiplier = Math.Clamp(margin, 0.5f, 2.0f);

        PlaySession session = renderer.PlaySession;
        HitBoundaries bounds = session.GetBoundaries(renderer.PlayInput.MarginMultiplier);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted($"可计数 {bounds.Counted:0.##}°  /  Perfect {bounds.Perfect:0.#}°  /  纯 Perfect {bounds.Pure:0.#}°");
        ImGui.PopTextWrapPos();

        if (renderer.IsPlaying && session.TargetFloor >= 0)
            ImGui.TextUnformatted($"{(session.Holding ? "长按中" : "下一块")} #{session.TargetFloor:N0}");
        else if (session.Completed)
            ImGui.TextUnformatted("手打完成，按 Enter 重玩");
        else if (!session.Failed)
            ImGui.TextUnformatted("按 Enter 开始或继续");

        HitRecord? last = session.LastHit;
        if (_lastHitShown != last)
        {
            _lastHitShown = last;
            _lastHitSeenAt = renderer.ElapsedSeconds;
        }

        if (last is { } shown && !renderer.HiddenFloorJudgment(shown.Floor)
            && renderer.ElapsedSeconds - _lastHitSeenAt < LastHitDisplaySeconds)
        {
            ImGui.TextUnformatted($"判定 {HitJudge.Label(shown.Margin)} · 偏差 {shown.OffsetDegrees:+0.00;-0.00;0.00}°");
        }
    }

    private static void DrawAudio(LevelRenderer renderer, AudioCalibrationWindow calibration)
    {
        ImGui.SeparatorText("音量");
        float music = renderer.MusicVolume * 100f;
        float hitsound = renderer.HitsoundVolume * 100f;
        ImGui.SetNextItemWidth(-64f);
        if (ImGui.SliderFloat("音乐", ref music, 0f, 100f, "%.0f%%", 0) != 0)
            renderer.MusicVolume = music / 100f;
        ImGui.SetNextItemWidth(-64f);
        if (ImGui.SliderFloat("打击音", ref hitsound, 0f, 100f, "%.0f%%", 0) != 0)
            renderer.HitsoundVolume = hitsound / 100f;

        ImGui.SeparatorText("音画校准");
        AudioCalibrationWindow.DrawDelay(renderer);
        if (ImGui.Button("打开校准场景", default) != 0)
            calibration.Open();
    }

    private void DrawPerformance(LevelRenderer renderer)
    {
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("性能", 0) == 0)
            return;

        double now = renderer.ElapsedSeconds;
        if (now - _fpsSampleAt >= 1.0)
        {
            _fpsSampleAt = now;
            _fpsDisplay = renderer.Fps;
        }

        ImGui.TextUnformatted($"{_fpsDisplay:0.0} FPS · 绘制 {renderer.DrawCalls:N0} 次");
        ImGui.TextUnformatted($"三角形 {renderer.TriangleCount:N0} · 缩放 {renderer.Camera.Zoom * 100f:0.0}%");
        if (renderer.LoadTimings.Count == 0 || ImGui.TreeNode("加载耗时") == 0)
            return;

        foreach ((string phase, double milliseconds) in renderer.LoadTimings)
            ImGui.TextUnformatted($"{phase}  {milliseconds:0.0} ms");
        ImGui.TextUnformatted($"总计 {renderer.LoadTotalMilliseconds:0.0} ms");
        ImGui.TreePop();
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds))
            return "0:00";

        int minutes = (int)(seconds / 60.0);
        return $"{minutes}:{seconds % 60:00.0}";
    }
}
