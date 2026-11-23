using ImGui4NET;
using static ImGui4NET.ImGuiChildFlags;
using static ImGui4NET.ImGuiCol;
using static ImGui4NET.ImGuiStyleVar;
using static ImGui4NET.ImGuiWindowFlags;

namespace SharpFAI.Player;

public sealed unsafe class ApplicationSettingsWindow
{
    private const string Popup = "###application-settings";
    private static readonly int[] FrameLimits = [0, 30, 60, 120, 144, 240];
    private static readonly string?[] Languages = [null, "en", "zh-Hans"];
    private static readonly ImVec4 Accent = Color(0.39f, 0.89f, 0.78f);
    private static readonly ImVec4 Muted = Color(0.64f, 0.68f, 0.70f);
    private static readonly ImVec4 Amber = Color(1f, 0.75f, 0.42f);
    private static readonly (ImGuiCol Role, ImVec4 Value)[] Palette =
    [
        (ImGuiCol_Text, Color(0.93f, 0.95f, 0.95f)),
        (ImGuiCol_TextDisabled, Muted),
        (ImGuiCol_PopupBg, Color(0.075f, 0.087f, 0.090f)),
        (ImGuiCol_ChildBg, Color(0.103f, 0.118f, 0.120f)),
        (ImGuiCol_Border, Color(0.22f, 0.26f, 0.27f)),
        (ImGuiCol_Separator, Color(0.21f, 0.25f, 0.26f)),
        (ImGuiCol_ModalWindowDimBg, Color(0.025f, 0.032f, 0.035f, 0.79f)),
        (ImGuiCol_FrameBg, Color(0.15f, 0.18f, 0.19f)),
        (ImGuiCol_FrameBgHovered, Color(0.18f, 0.26f, 0.26f)),
        (ImGuiCol_FrameBgActive, Color(0.19f, 0.32f, 0.30f)),
        (ImGuiCol_CheckMark, Accent),
        (ImGuiCol_SliderGrab, Accent),
        (ImGuiCol_SliderGrabActive, Color(0.63f, 0.99f, 0.91f)),
        (ImGuiCol_Header, Color(0.13f, 0.29f, 0.27f)),
        (ImGuiCol_HeaderHovered, Color(0.17f, 0.35f, 0.32f)),
        (ImGuiCol_HeaderActive, Color(0.21f, 0.42f, 0.37f)),
        (ImGuiCol_Button, Color(0.15f, 0.18f, 0.20f)),
        (ImGuiCol_ButtonHovered, Color(0.19f, 0.28f, 0.29f)),
        (ImGuiCol_ButtonActive, Color(0.23f, 0.36f, 0.36f)),
        (ImGuiCol_NavCursor, Accent),
    ];

    private enum Page { Display, Audio, Interface }

    private ApplicationSettings _original = ApplicationSettings.PlayerDefaults;
    private ApplicationSettings _draft = ApplicationSettings.PlayerDefaults;
    private Page _page;
    private bool _opening;
    private bool _open;
    private bool _saveFailed;
    private bool _choiceOpen;
    private bool _enabledKeyboardNavigation;
    private bool _chinese;
    private string Tr(string zh, string en) => _chinese ? zh : en;

    public bool IsOpen => _opening || _open;

    public void BeginFrame(ImGuiIO* io)
    {
        const int keyboard = (int)ImGuiConfigFlags.ImGuiConfigFlags_NavEnableKeyboard;
        if (IsOpen && (io->ConfigFlags & keyboard) == 0)
        {
            io->ConfigFlags |= keyboard;
            _enabledKeyboardNavigation = true;
        }
        else if (!IsOpen && _enabledKeyboardNavigation)
        {
            io->ConfigFlags &= ~keyboard;
            _enabledKeyboardNavigation = false;
        }
    }

    public void Open(ApplicationSettings current)
    {
        _original = _draft = current;
        _page = Page.Display;
        _saveFailed = false;
        _choiceOpen = false;
        _opening = true;
    }

    public void Draw(bool chinese, bool editor, ApplicationSettings defaults, Func<ApplicationSettings, bool> tryApply)
    {
        if (!IsOpen)
            return;

        _chinese = chinese;
        if (_opening)
        {
            ImGui.OpenPopup(Popup, 0);
            _opening = false;
            _open = true;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(Vec(viewport->WorkPos.x + viewport->WorkSize.x * 0.5f,
            viewport->WorkPos.y + viewport->WorkSize.y * 0.5f), 0, Vec(0.5f, 0.5f));
        ImGui.SetNextWindowSize(Vec(Math.Clamp(viewport->WorkSize.x - 28f, 1f, 920f),
            Math.Clamp(viewport->WorkSize.y - 28f, 1f, 580f)));

        PushStyle();
        if (ImGui.BeginPopupModal(Popup, (int)(ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoSavedSettings)) != 0)
        {
            bool childHadKeyboard = _choiceOpen || ImGui.GetIO()->WantTextInput != 0;
            _choiceOpen = false;
            DrawHeader(editor);
            float footerHeight = ImGui.GetFrameHeight() + ImGui.GetTextLineHeightWithSpacing() + 20f;
            float bodyHeight = Math.Max(40f, ImGui.GetContentRegionAvail().y - footerHeight);
            float available = ImGui.GetContentRegionAvail().x;
            if (available < 610f)
            {
                float tabs = 24f + 2f * (ImGui.GetStyle()->WindowPadding.y + ImGui.GetStyle()->ChildBorderSize);
                DrawNavigation(available, tabs, horizontal: true);
                DrawPage(Math.Max(40f, bodyHeight - tabs - ImGui.GetStyle()->ItemSpacing.y), editor);
            }
            else
            {
                DrawNavigation(190f, bodyHeight, horizontal: false);
                ImGui.SameLine();
                DrawPage(bodyHeight, editor);
            }
            DrawFooter(defaults, tryApply, childHadKeyboard);
            ImGui.EndPopup();
        }
        else if (ImGui.IsPopupOpen(Popup) == 0)
            _open = false;
        ImGui.PopStyleVar(8);
        ImGui.PopStyleColor(Palette.Length);
    }

    private void DrawHeader(bool editor)
    {
        ImGui.TextColored(Accent, editor ? "SHARPFAI  /  EDITOR" : "SHARPFAI  /  PLAYER");
        ImGui.PushFont(null, 29f);
        ImGui.TextUnformatted(Tr("设置", "Settings"));
        ImGui.PopFont();
        ImGui.Separator();
    }

    private void DrawNavigation(float width, float height, bool horizontal)
    {
        if (ImGui.BeginChild("##settings-categories", Vec(width, height),
                (int)(ImGuiChildFlags_Borders | ImGuiChildFlags_AlwaysUseWindowPadding)) != 0)
        {
            float buttonWidth = horizontal ? Math.Max(1, (ImGui.GetContentRegionAvail().x - 2f * ImGui.GetStyle()->ItemSpacing.x) / 3f) : 0;
            Category(Page.Display, Tr("显示", "Display"), buttonWidth, horizontal);
            Category(Page.Audio, Tr("音频", "Audio"), buttonWidth, horizontal);
            Category(Page.Interface, Tr("界面", "Interface"), buttonWidth, horizontal);
        }
        ImGui.EndChild();
    }

    private void Category(Page page, string label, float width, bool horizontal)
    {
        if (horizontal && page != Page.Display)
            ImGui.SameLine();
        bool selected = _page == page;
        string text = horizontal ? label : $"{(int)page + 1:00}    {label}";
        if (selected)
            ImGui.PushStyleColor((int)ImGuiCol_Text, Accent);
        if (ImGui.Selectable($"{text}###settings-page-{(int)page}", (byte)(selected ? 1 : 0), 0,
                Vec(width, horizontal ? 24f : 37f)) != 0)
            _page = page;
        if (selected)
        {
            ImGui.PopStyleColor();
            ImGui.SetItemDefaultFocus();
        }
    }

    private void DrawPage(float height, bool editor)
    {
        if (ImGui.BeginChild($"##settings-content-{(int)_page}", Vec(0, height),
                (int)(ImGuiChildFlags_Borders | ImGuiChildFlags_AlwaysUseWindowPadding)) != 0)
        {
            switch (_page)
            {
                case Page.Display:
                    Heading(Tr("显示", "Display"));
                    if (Toggle("vsync", Tr("垂直同步", "Vertical sync"), _draft.VSync, out bool sync))
                        Change(_draft with { VSync = sync });
                    if (Choice("frame-limit", Tr("帧率上限", "Frame rate limit"), FrameLimits, _draft.FrameLimit,
                            value => value == 0 ? Tr("不限", "Unlimited") : $"{value} FPS", out int limit))
                        Change(_draft with { FrameLimit = limit });
                    break;
                case Page.Audio:
                    Heading(Tr("音频", "Audio"));
                    if (Toggle("skip-audio", Tr("不加载音频（下次加载生效）", "Skip audio on next level load"),
                            _draft.SkipAudio, out bool skipAudio))
                        Change(_draft with { SkipAudio = skipAudio });
                    if (Volume("master", Tr("总音量", "Master volume"), _draft.MasterVolume, out int master))
                        Change(_draft with { MasterVolume = master });
                    if (Volume("music", Tr("音乐音量", "Music volume"), _draft.MusicVolume, out int music))
                        Change(_draft with { MusicVolume = music });
                    if (Volume("hitsound", Tr("打击音音量", "Hitsound volume"), _draft.HitsoundVolume, out int hitsound))
                        Change(_draft with { HitsoundVolume = hitsound });
                    break;
                case Page.Interface:
                    Heading(Tr("界面", "Interface"));
                    if (Toggle("fps", Tr("显示菜单栏帧率", "Show FPS in menu bar"), _draft.ShowFrameRate, out bool fps))
                        Change(_draft with { ShowFrameRate = fps });
                    if (editor && Choice("language", Tr("界面语言", "Interface language"), Languages, _draft.Language,
                            value => value switch { "en" => "English", "zh-Hans" => "简体中文", _ => Tr("跟随系统", "System default") },
                            out string? language))
                        Change(_draft with { Language = language });
                    break;
            }
        }
        ImGui.EndChild();
    }

    private static void Heading(string title)
    {
        ImGui.PushFont(null, 21f);
        ImGui.TextUnformatted(title);
        ImGui.PopFont();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void Row(string label, bool fullWidth = false)
    {
        float width = ImGui.GetContentRegionAvail().x;
        float controlWidth = Math.Min(260f, Math.Max(1, width * 0.45f));
        float x = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        float indent = fullWidth ? width - controlWidth : width - ImGui.GetFrameHeight();
        if (ImGui.CalcTextSize(label, null, 0, -1f).x + 20f < indent)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(x + indent);
        }
        ImGui.SetNextItemWidth(Math.Min(controlWidth, ImGui.GetContentRegionAvail().x));
    }

    private static bool Toggle(string id, string label, bool value, out bool result)
    {
        Row(label);
        result = value;
        bool changed = ImGui.Checkbox($"##{id}", ref result) != 0;
        ImGui.Spacing();
        return changed;
    }

    private static bool Volume(string id, string label, int value, out int result)
    {
        Row(label, fullWidth: true);
        result = value;
        bool changed = ImGui.SliderInt($"##{id}", ref result, 0, 100, "%d%%") != 0;
        ImGui.Spacing();
        return changed;
    }

    private bool Choice<T>(string id, string label, T[] values, T current, Func<T, string> format, out T result)
    {
        Row(label, fullWidth: true);
        result = current;
        bool changed = false;
        if (ImGui.BeginCombo($"##{id}", format(current)) != 0)
        {
            _choiceOpen = true;
            foreach (T value in values)
            {
                bool selected = EqualityComparer<T>.Default.Equals(value, current);
                if (ImGui.Selectable(format(value), (byte)(selected ? 1 : 0), 0, default) != 0)
                {
                    result = value;
                    changed = !selected;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.Spacing();
        return changed;
    }

    private void DrawFooter(ApplicationSettings defaults, Func<ApplicationSettings, bool> tryApply, bool comboWasOpen)
    {
        ImGui.Separator();
        bool dirty = _draft != _original;
        ImGui.TextColored(_saveFailed || dirty ? Amber : Muted, _saveFailed ? Tr("保存失败，设置未应用", "Could not save settings") :
            dirty ? Tr("有未应用的更改", "Unsaved changes") : Tr("无未应用的更改", "No pending changes"));

        float remaining = ImGui.GetContentRegionAvail().x;
        float gap = ImGui.GetStyle()->ItemSpacing.x;
        float buttonWidth = Math.Min(122f, Math.Max(1f, (remaining - 2 * gap) / 3f));
        if (ImGui.Button(Tr("恢复默认", "Reset defaults") + "###settings-reset", Vec(buttonWidth, 0)) != 0)
            Change(defaults);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, remaining - 3f * buttonWidth - 2f * gap));
        bool cancel = ImGui.Button(Tr("取消", "Cancel") + "###settings-cancel", Vec(buttonWidth, 0)) != 0;
        ImGui.SameLine();
        ImGui.PushStyleColor((int)ImGuiCol_Text, Color(0.04f, 0.12f, 0.11f));
        ImGui.PushStyleColor((int)ImGuiCol_Button, Accent);
        ImGui.PushStyleColor((int)ImGuiCol_ButtonHovered, Color(0.60f, 0.97f, 0.88f));
        ImGui.PushStyleColor((int)ImGuiCol_ButtonActive, Color(0.30f, 0.76f, 0.66f));
        ImGui.BeginDisabled((byte)(_draft == _original ? 1 : 0));
        bool apply = ImGui.Button(Tr("应用", "Apply") + "###settings-apply", Vec(buttonWidth, 0)) != 0;
        ImGui.EndDisabled();
        ImGui.PopStyleColor(4);

        bool escape = !comboWasOpen && ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0;
        if (apply)
            _saveFailed = !tryApply(_draft);
        if (cancel || escape || (apply && !_saveFailed))
        {
            ImGui.CloseCurrentPopup();
            _open = false;
        }
    }

    private void Change(ApplicationSettings settings)
    {
        _draft = settings;
        _saveFailed = false;
    }

    private static void PushStyle()
    {
        foreach (var (role, value) in Palette)
            ImGui.PushStyleColor((int)role, value);
        ImGui.PushStyleVar((int)ImGuiStyleVar_WindowPadding, Vec(18, 12));
        ImGui.PushStyleVar((int)ImGuiStyleVar_FramePadding, Vec(11, 7));
        ImGui.PushStyleVar((int)ImGuiStyleVar_ItemSpacing, Vec(8, 9));
        ImGui.PushStyleVar((int)ImGuiStyleVar_WindowRounding, 8f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_PopupRounding, 8f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_ChildRounding, 5f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_FrameRounding, 4f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_GrabRounding, 3f);
    }

    private static ImVec2 Vec(float x, float y) => new() { x = x, y = y };
    private static ImVec4 Color(float r, float g, float b, float a = 1) => new() { x = r, y = g, z = b, w = a };
}
