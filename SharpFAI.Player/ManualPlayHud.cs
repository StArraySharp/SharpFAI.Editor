using ImGui4NET;
using SharpFAI.Player.Engine;
using SharpFAI.Player.Engine.Play;

namespace SharpFAI.Player;

public static unsafe class ManualPlayHud
{
    private static readonly (HitMargin Margin, uint Color)[] Judgments =
    [
        (HitMargin.Perfect, 0xFF80E2A2),
        (HitMargin.EarlyPerfect, 0xFFABE6BB),
        (HitMargin.LatePerfect, 0xFFABE6BB),
        (HitMargin.VeryEarly, 0xFFFFD687),
        (HitMargin.VeryLate, 0xFFFFD687),
        (HitMargin.TooEarly, 0xFF86AFFF),
        (HitMargin.TooLate, 0xFF86AFFF),
        (HitMargin.Miss, 0xFF9090FF),
        (HitMargin.Overload, 0xFF9090FF),
        (HitMargin.Auto, 0xFFDED7CD),
    ];

    public static void Draw(LevelRenderer renderer)
    {
        ImGuiViewport* viewport = ImGui.GetMainViewport();
        ImVec2 end = new() { x = viewport->WorkPos.x + viewport->WorkSize.x, y = viewport->WorkPos.y + viewport->WorkSize.y };
        Draw(renderer, ImGui.GetForegroundDrawList(viewport), viewport->Pos, end, viewport->WorkPos.y, null);
    }

    public static void Draw(LevelRenderer renderer, ImDrawList* draw, ImVec2 min, ImVec2 max, Func<string, string>? localize = null)
        => Draw(renderer, draw, min, max, min.y, localize);

    private static void Draw(LevelRenderer renderer, ImDrawList* draw, ImVec2 min, ImVec2 max, float top, Func<string, string>? localize)
    {
        PlaySession session = renderer.PlaySession;
        if (!renderer.ManualPlayEnabled || renderer.IsLoading || !session.IsTracking)
            return;

        DrawJudgments(draw, max.x, top, session.Stats, localize);

        ImVec2 center = new()
        {
            x = (min.x + max.x) * .5f,
            y = (min.y + max.y) * .5f,
        };
        if (session.Failed)
        {
            string prompt = session.CheckpointFloor > 0
                ? string.Format(localize?.Invoke("HudRetryCheckpoint") ?? "按任意键从 #{0:N0} 重试", session.CheckpointFloor)
                : localize?.Invoke("HudRetry") ?? "按任意键重新开始";
            DrawCentered(draw, center, prompt, ImGui.GetFontSize() * 1.5f);
        }
        else if (renderer.IsPlaying && session.Countdown is int remaining)
        {
            DrawCentered(draw, center, remaining == 0 ? "GO!" : remaining.ToString(), ImGui.GetFontSize() * 4f);
        }
    }

    private static void DrawJudgments(ImDrawList* draw, float rightEdge, float topEdge, HitStats stats, Func<string, string>? localize)
    {
        const float inset = 12f;
        const float padding = 10f;
        const float columnGap = 18f;
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        string summary = string.Format(localize?.Invoke("HudSummary") ?? "总计 {0:N0}   {1:P2}", stats.Total, stats.Accuracy);
        float labelWidth = 0;
        foreach ((HitMargin margin, _) in Judgments)
            labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize(Label(margin), null, 0, -1f).x);
        float countWidth = ImGui.CalcTextSize(stats.Total.ToString("N0"), null, 0, -1f).x;
        float width = Math.Max(labelWidth + columnGap + countWidth,
            ImGui.CalcTextSize(summary, null, 0, -1f).x) + padding * 2;
        float right = rightEdge - inset;
        float left = right - width;
        float top = topEdge + inset;
        float bottom = top + (Judgments.Length + 1) * rowHeight + padding * 2;
        ImDrawList.AddRectFilled(draw, new ImVec2 { x = left, y = top }, new ImVec2 { x = right, y = bottom },
            0xD9222020, 6f, 0);

        float y = top + padding;
        foreach ((HitMargin margin, uint color) in Judgments)
        {
            string count = stats[margin].ToString("N0");
            float countX = right - padding - ImGui.CalcTextSize(count, null, 0, -1f).x;
            ImDrawList.AddText(draw, new ImVec2 { x = left + padding, y = y }, color, Label(margin));
            ImDrawList.AddText(draw, new ImVec2 { x = countX, y = y }, color, count);
            y += rowHeight;
        }

        ImDrawList.AddText(draw, new ImVec2 { x = left + padding, y = y }, 0xFFFFFFFF, summary);

        string Label(HitMargin margin) => localize?.Invoke("Hud_" + margin) ?? HitJudge.Label(margin);
    }

    private static void DrawCentered(ImDrawList* draw, ImVec2 center, string text, float fontSize)
    {
        ImFont* font = ImGui.GetFont();
        ImVec2 size = ImGui.CalcTextSize(text, null, 0, -1f);
        float scale = fontSize / ImGui.GetFontSize();
        ImVec2 position = new() { x = center.x - size.x * scale * .5f, y = center.y - size.y * scale * .5f };
        ImVec2 shadow = new() { x = position.x + 2f, y = position.y + 2f };
        ImDrawList.AddText(draw, font, fontSize, in shadow, 0xE0000000, text, null, 0f);
        ImDrawList.AddText(draw, font, fontSize, in position, 0xFFFFFFFF, text, null, 0f);
    }
}
