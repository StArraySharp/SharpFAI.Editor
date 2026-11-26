using ImGui4NET;
using static ImGui4NET.ImGuiChildFlags;
using static ImGui4NET.ImGuiCol;
using static ImGui4NET.ImGuiStyleVar;
using static ImGui4NET.ImGuiWindowFlags;

namespace SharpFAI.Player;

public sealed unsafe class ApplicationSettingsWindow
{
    private const string Popup = "###application-settings";
    private static readonly ApplicationWindowMode[] WindowModes = Enum.GetValues<ApplicationWindowMode>();
    private static readonly int[] FrameLimits = [0, 30, 60, 120, 144, 240];
    private static readonly int[] TextureScales = [100, 75, 50, 25];
    private static readonly string?[] Languages = [null, "en", "zh-Hans"];
    private static readonly ImVec4 Accent = Color(0.62f, 0.78f, 1f);
    private static readonly ImVec4 Muted = Color(0.66f, 0.71f, 0.79f);
    private static readonly ImVec4 Amber = Color(1f, 0.75f, 0.42f);
    private static readonly (ImGuiCol Role, ImVec4 Value)[] Palette =
    [
        (ImGuiCol_Text, Color(0.93f, 0.95f, 0.98f)),
        (ImGuiCol_TextDisabled, Muted),
        (ImGuiCol_PopupBg, Color(0.08f, 0.10f, 0.14f)),
        (ImGuiCol_ChildBg, Color(0.105f, 0.13f, 0.18f)),
        (ImGuiCol_Border, Color(0.23f, 0.27f, 0.35f)),
        (ImGuiCol_Separator, Color(0.22f, 0.26f, 0.34f)),
        (ImGuiCol_ModalWindowDimBg, Color(0.02f, 0.028f, 0.04f, 0.72f)),
        (ImGuiCol_FrameBg, Color(0.15f, 0.19f, 0.26f)),
        (ImGuiCol_FrameBgHovered, Color(0.20f, 0.27f, 0.38f)),
        (ImGuiCol_FrameBgActive, Color(0.24f, 0.34f, 0.50f)),
        (ImGuiCol_CheckMark, Accent),
        (ImGuiCol_SliderGrab, Accent),
        (ImGuiCol_SliderGrabActive, Color(0.80f, 0.90f, 1f)),
        (ImGuiCol_Header, Color(0.20f, 0.30f, 0.46f)),
        (ImGuiCol_HeaderHovered, Color(0.23f, 0.35f, 0.52f)),
        (ImGuiCol_HeaderActive, Color(0.27f, 0.42f, 0.62f)),
        (ImGuiCol_Button, Color(0.16f, 0.20f, 0.27f)),
        (ImGuiCol_ButtonHovered, Color(0.22f, 0.29f, 0.41f)),
        (ImGuiCol_ButtonActive, Color(0.26f, 0.37f, 0.53f)),
        (ImGuiCol_NavCursor, Accent),
    ];

    private enum Page { Display, Graphics, Effects, Audio, Interface, Mods, Editing }
    private static readonly Page[] EditorPages = Enum.GetValues<Page>();
    private static readonly Page[] PlayerPages = [Page.Display, Page.Graphics, Page.Effects, Page.Audio, Page.Interface];

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
        _saveFailed = false;
        _choiceOpen = false;
        _opening = true;
    }

    public void Draw(bool chinese, bool editor, ApplicationSettings defaults, Func<ApplicationSettings, bool> tryApply,
        Action? drawMods = null)
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
        ImGui.SetNextWindowSize(Vec(Math.Clamp(viewport->WorkSize.x - 24f, 1f, 820f),
            Math.Clamp(viewport->WorkSize.y - 24f, 1f, 530f)));

        PushStyle();
        if (ImGui.BeginPopupModal(Popup, (int)(ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize |
            ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoSavedSettings)) != 0)
        {
            bool childHadKeyboard = _choiceOpen || ImGui.GetIO()->WantTextInput != 0;
            _choiceOpen = false;
            bool close = DrawHeader(editor);
            float available = ImGui.GetContentRegionAvail().x;
            bool statusAbove = available < 535f;
            float footerHeight = ImGui.GetFrameHeight() + 2f * ImGui.GetStyle()->ItemSpacing.y + 2f;
            if (statusAbove)
                footerHeight += ImGui.GetTextLineHeightWithSpacing();
            float bodyHeight = Math.Max(1f, ImGui.GetContentRegionAvail().y - footerHeight);
            Page[] pages = editor ? EditorPages : PlayerPages;
            if (available < 610f)
            {
                int columns = available >= 340f ? 3 : 2;
                int rows = (pages.Length + columns - 1) / columns;
                float gap = ImGui.GetStyle()->ItemSpacing.y;
                float tabs = rows * ImGui.GetFrameHeight() + (rows - 1) * gap + 2f * ImGui.GetStyle()->WindowPadding.y;
                DrawNavigation(available, tabs, pages, columns);
                DrawPage(Math.Max(1f, bodyHeight - tabs - gap), editor, drawMods);
            }
            else
            {
                DrawNavigation(166f, bodyHeight, pages, 1);
                ImGui.SameLine();
                DrawPage(bodyHeight, editor, drawMods);
            }
            DrawFooter(defaults, tryApply, childHadKeyboard, statusAbove, close);
            ImGui.EndPopup();
        }
        else if (ImGui.IsPopupOpen(Popup) == 0)
            _open = false;
        ImGui.PopStyleVar(8);
        ImGui.PopStyleColor(Palette.Length);
    }

    private bool DrawHeader(bool editor)
    {
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().x;
        ImGui.PushFont(null, 25f);
        ImGui.TextUnformatted(Tr("设置", "Settings"));
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextDisabled(editor ? Tr("编辑器", "Editor") : Tr("播放器", "Player"));
        ImGui.SameLine();
        float size = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - size));
        bool close = ImGui.Button("##settings-close", Vec(size, size)) != 0;
        ImVec2 min = ImGui.GetItemRectMin(), max = ImGui.GetItemRectMax();
        float x = (min.x + max.x) * .5f, y = (min.y + max.y) * .5f, radius = size * .15f;
        ImDrawList* draw = ImGui.GetWindowDrawList();
        uint color = ImGui.GetColorU32((int)ImGuiCol_Text, 1f);
        ImDrawList.AddLine(draw, Vec(x - radius, y - radius), Vec(x + radius, y + radius), color, 1.5f);
        ImDrawList.AddLine(draw, Vec(x + radius, y - radius), Vec(x - radius, y + radius), color, 1.5f);
        if (ImGui.IsItemHovered() != 0)
            ImGui.SetTooltip(Tr("关闭设置", "Close settings"));
        ImGui.Separator();
        return close;
    }

    private void DrawNavigation(float width, float height, Page[] pages, int columns)
    {
        if (ImGui.BeginChild("##settings-categories", Vec(width, height),
                (int)(ImGuiChildFlags_Borders | ImGuiChildFlags_AlwaysUseWindowPadding),
                (int)ImGuiWindowFlags_NoScrollbar) != 0)
        {
            float gap = ImGui.GetStyle()->ItemSpacing.x;
            float buttonWidth = Math.Max(1f, (ImGui.GetContentRegionAvail().x - (columns - 1) * gap) / columns);
            for (int index = 0; index < pages.Length; index++)
            {
                if (index % columns != 0)
                    ImGui.SameLine();
                Category(pages[index], buttonWidth);
            }
        }
        ImGui.EndChild();
    }

    private void Category(Page page, float width)
    {
        bool selected = _page == page;
        ImGui.PushStyleColor((int)ImGuiCol_Button, selected ? Color(0.20f, 0.30f, 0.46f) : Color(0, 0, 0, 0));
        ImGui.PushStyleColor((int)ImGuiCol_Text, selected ? Accent : Muted);
        if (ImGui.Button($"{PageTitle(page)}###settings-page-{(int)page}", Vec(width, ImGui.GetFrameHeight())) != 0)
            _page = page;
        ImGui.PopStyleColor(2);
        if (selected)
            ImGui.SetItemDefaultFocus();
    }

    private string PageTitle(Page page) => page switch
    {
        Page.Display => Tr("显示", "Display"),
        Page.Graphics => Tr("画面", "Graphics"),
        Page.Effects => Tr("特效过滤", "Effects"),
        Page.Audio => Tr("音频", "Audio"),
        Page.Interface => Tr("界面", "Interface"),
        Page.Mods => Tr("Mod 管理", "Mods"),
        Page.Editing => Tr("轨道编辑", "Track editing"),
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    private void DrawPage(float height, bool editor, Action? drawMods)
    {
        if (ImGui.BeginChild($"##settings-content-{(int)_page}", Vec(0, height),
                (int)(ImGuiChildFlags_Borders | ImGuiChildFlags_AlwaysUseWindowPadding)) != 0)
        {
            Heading(PageTitle(_page));
            switch (_page)
            {
                case Page.Display:
                    Section(Tr("窗口", "Window"));
                    if (Choice("window-mode", Tr("窗口模式", "Window mode"), WindowModes, _draft.WindowMode,
                            value => value == ApplicationWindowMode.Fullscreen ? Tr("全屏", "Fullscreen") : Tr("窗口化", "Windowed"),
                            out ApplicationWindowMode mode))
                        Change(_draft with { WindowMode = mode });
                    Note(Tr("Alt+Enter 切换全屏", "Alt+Enter toggles fullscreen"));
                    Section(Tr("帧率", "Frame pacing"));
                    if (Choice("frame-limit", Tr("帧率上限", "Frame rate limit"), FrameLimits, _draft.FrameLimit,
                            value => value == 0 ? Tr("不限", "Unlimited") : $"{value} FPS", out int limit))
                        Change(_draft with { FrameLimit = limit });
                    if (Toggle("vsync", Tr("垂直同步", "VSync"), _draft.VSync, out bool sync))
                        Change(_draft with { VSync = sync });
                    break;
                case Page.Graphics:
                    Section(Tr("星球", "Planets"));
                    if (PlanetColor("fire-color", Tr("火球颜色", "Fire planet color"), _draft.FirePlanetColor, out string fire))
                        Change(_draft with { FirePlanetColor = fire });
                    if (PlanetColor("ice-color", Tr("冰球颜色", "Ice planet color"), _draft.IcePlanetColor, out string ice))
                        Change(_draft with { IcePlanetColor = ice });
                    if (Percent("planet-trail", Tr("星球拖尾长度", "Planet trail length"),
                            _draft.PlanetTrailPercent, 200, out int trail))
                        Change(_draft with { PlanetTrailPercent = trail });
                    if (editor && Toggle("tiles-only-hide-planets", Tr("仅轨道时隐藏星球", "Hide planets in track-only mode"),
                            _draft.HidePlanetsInTilesOnly, out bool hidePlanets))
                        Change(_draft with { HidePlanetsInTilesOnly = hidePlanets });
                    Section(Tr("装饰纹理", "Decoration textures"));
                    if (Choice("decoration-textures", Tr("纹理尺寸", "Texture size"), TextureScales, _draft.DecorationTexturePercent,
                            value => value == 100 ? Tr("100% 原图", "100% Original") : $"{value}%", out int textureScale))
                        Change(_draft with { DecorationTexturePercent = textureScale });
                    Note(Tr("下次加载关卡时生效", "Applies the next time a level loads"));
                    break;
                case Page.Effects:
                    Section(Tr("场景特效", "Scene effects"));
                    if (Toggle("camera-filters", Tr("相机滤镜", "Camera filters"), _draft.CameraFilters, out bool filters))
                        Change(_draft with { CameraFilters = filters });
                    if (Toggle("screen-effects", Tr("辉光、滚动、平铺和残影", "Bloom, scroll, tiling and feedback"),
                            _draft.ScreenEffects, out bool effects))
                        Change(_draft with { ScreenEffects = effects });
                    if (Toggle("screen-flash", Tr("屏幕闪光", "Screen flashes"), _draft.ScreenFlash, out bool flash))
                        Change(_draft with { ScreenFlash = flash });
                    if (Toggle("camera-shake", Tr("相机震动", "Camera shake"), _draft.CameraShake, out bool shake))
                        Change(_draft with { CameraShake = shake });
                    Section(Tr("显示的装饰", "Visible decorations"));
                    if (BeginOptionsGrid("##decorations"))
                    {
                        DecorationToggle(DecorationKinds.Image, Tr("图片装饰", "Image decorations"));
                        DecorationToggle(DecorationKinds.Text, Tr("文字装饰", "Text decorations"));
                        DecorationToggle(DecorationKinds.Floor, Tr("轨道装饰", "Floor decorations"));
                        DecorationToggle(DecorationKinds.Planet, Tr("星球装饰", "Planet decorations"));
                        DecorationToggle(DecorationKinds.Particle, Tr("粒子装饰", "Particle decorations"));
                        ImGui.EndTable();
                    }
                    break;
                case Page.Audio:
                    Section(Tr("播放音量", "Playback volume"));
                    if (Percent("master", Tr("主音量", "Master volume"), _draft.MasterVolume, 100, out int master))
                        Change(_draft with { MasterVolume = master });
                    if (Percent("music", Tr("音乐音量", "Music volume"), _draft.MusicVolume, 100, out int music))
                        Change(_draft with { MusicVolume = music });
                    if (Percent("hitsound", Tr("打击音量", "Hitsound volume"), _draft.HitsoundVolume, 100, out int hitsound))
                        Change(_draft with { HitsoundVolume = hitsound });
                    Section(Tr("音频加载", "Audio loading"));
                    if (Toggle("skip-audio", Tr("跳过音频加载", "Skip audio loading"), _draft.SkipAudio, out bool skipAudio))
                        Change(_draft with { SkipAudio = skipAudio });
                    Note(Tr("下次加载关卡时生效", "Applies the next time a level loads"));
                    break;
                case Page.Interface:
                    if (Toggle("fps", Tr("显示菜单栏帧率", "Show FPS in menu bar"), _draft.ShowFrameRate, out bool fps))
                        Change(_draft with { ShowFrameRate = fps });
                    if (editor && Choice("language", Tr("语言", "Language"), Languages, _draft.Language,
                            value => value switch { "en" => "English", "zh-Hans" => "简体中文", _ => Tr("跟随系统", "System default") },
                            out string? language))
                        Change(_draft with { Language = language });
                    break;
                case Page.Editing:
                    DrawTrackEditingOptions();
                    break;
                case Page.Mods:
                    drawMods?.Invoke();
                    break;
            }
        }
        ImGui.EndChild();
    }

    private void DrawTrackEditingOptions()
    {
        Section(Tr("选区统计", "Selection statistics"));
        if (Toggle("selection-stats", Tr("显示选区统计", "Show selection statistics"), _draft.ShowSelectionStatistics, out bool show))
            Change(_draft with { ShowSelectionStatistics = show });
        ImGui.BeginDisabled((byte)(_draft.ShowSelectionStatistics ? 0 : 1));
        if (BeginOptionsGrid("##selection-fields"))
        {
            StatisticToggle(SelectionStatisticsFields.Angle, Tr("总角度", "Total angle"));
            StatisticToggle(SelectionStatisticsFields.Beats, Tr("拍数", "Beats"));
            StatisticToggle(SelectionStatisticsFields.Count, Tr("砖块数", "Tile count"));
            StatisticToggle(SelectionStatisticsFields.Duration, Tr("时长", "Duration"));
            ImGui.EndTable();
        }
        if (Toggle("selection-tulttak", Tr("计时排除选区最后一块", "Exclude last selected tile from timing"),
                _draft.ExcludeLastSelectedTileFromTiming, out bool exclude))
            Change(_draft with { ExcludeLastSelectedTileFromTiming = exclude });
        ImGui.EndDisabled();

        Section(Tr("旋转和翻转", "Rotation and flips"));
        if (Toggle("fine-rotation", Tr("启用自定义旋转步长", "Enable custom rotation step"),
                _draft.FineTrackRotation, out bool fineRotation))
            Change(_draft with { FineTrackRotation = fineRotation });
        Note("Ctrl+Alt+, / .");
        ImGui.BeginDisabled((byte)(_draft.FineTrackRotation ? 0 : 1));
        Row(Tr("旋转步长", "Rotation step"));
        float step = _draft.TrackRotationStep;
        if (ImGui.DragFloat("##rotation-step", ref step, .1f, .001f, 360f, "%.3f°",
                (int)ImGuiSliderFlags.ImGuiSliderFlags_AlwaysClamp) != 0 && float.IsFinite(step))
            Change(_draft with { TrackRotationStep = step });
        ImGui.Spacing();
        ImGui.EndDisabled();

        if (Toggle("sync-track-events", Tr("同步变换事件坐标", "Transform event coordinates with track"),
                _draft.SyncTrackEventPositions, out bool sync))
            Change(_draft with { SyncTrackEventPositions = sync });

        void StatisticToggle(SelectionStatisticsFields field, string label)
        {
            ImGui.TableNextColumn();
            if (Toggle($"stat-{field}", label, (_draft.SelectionStatistics & field) != 0, out bool enabled))
                Change(_draft with { SelectionStatistics = enabled ? _draft.SelectionStatistics | field : _draft.SelectionStatistics & ~field });
        }
    }

    private bool BeginOptionsGrid(string id)
    {
        int columns = ImGui.GetContentRegionAvail().x >= (_chinese ? 360f : 490f) ? 2 : 1;
        return ImGui.BeginTable(id, columns, (int)ImGuiTableFlags.ImGuiTableFlags_SizingStretchSame, default, 0) != 0;
    }

    private static void Heading(string title)
    {
        ImGui.PushFont(null, 22f);
        ImGui.TextUnformatted(title);
        ImGui.PopFont();
        ImGui.Spacing();
    }

    private static void Section(string title)
    {
        ImGui.PushStyleColor((int)ImGuiCol_Text, Muted);
        ImGui.SeparatorText(title);
        ImGui.PopStyleColor();
    }

    private static void Note(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }

    private static void Row(string label)
    {
        float width = ImGui.GetContentRegionAvail().x;
        float controlWidth = Math.Clamp(width * 0.48f, 1f, 270f);
        float x = ImGui.GetCursorPosX();
        if (ImGui.CalcTextSize(label, null, 0, -1f).x + 16f < width - controlWidth)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
            ImGui.SameLine();
            ImGui.SetCursorPosX(x + width - controlWidth);
        }
        else
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextUnformatted(label);
            ImGui.PopTextWrapPos();
            controlWidth = width;
        }
        ImGui.SetNextItemWidth(Math.Max(1, Math.Min(controlWidth, ImGui.GetContentRegionAvail().x)));
    }

    private static bool Toggle(string id, string label, bool value, out bool result)
    {
        result = value;
        return ImGui.Checkbox($"{label}###{id}", ref result) != 0;
    }

    private void DecorationToggle(DecorationKinds kind, string label)
    {
        ImGui.TableNextColumn();
        if (Toggle($"decoration-{kind}", label, (_draft.VisibleDecorations & kind) != 0, out bool visible))
            Change(_draft with
            {
                VisibleDecorations = visible ? _draft.VisibleDecorations | kind : _draft.VisibleDecorations & ~kind,
            });
    }

    private bool PlanetColor(string id, string label, string value, out string result)
    {
        Row(label);
        ApplicationSettings.TryParsePlanetColor(value, out int rgb);
        float* color = stackalloc float[3] { (rgb >> 16) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f };
        bool changed = ImGui.ColorEdit3($"##{id}", color, (int)ImGuiColorEditFlags.ImGuiColorEditFlags_DisplayHex) != 0;
        _choiceOpen |= ImGui.IsPopupOpen((string?)null, (int)ImGuiPopupFlags.ImGuiPopupFlags_AnyPopupId) != 0;
        result = changed ? ApplicationSettings.FormatPlanetColor(
            (ToByte(color[0]) << 16) | (ToByte(color[1]) << 8) | ToByte(color[2])) : value;
        return changed;

        static int ToByte(float channel) => (int)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f);
    }

    private static bool Percent(string id, string label, int value, int maximum, out int result)
    {
        Row(label);
        result = value;
        bool changed = ImGui.SliderInt($"##{id}", ref result, 0, maximum, "%d%%") != 0;
        result = Math.Clamp(result, 0, maximum);
        return changed;
    }

    private bool Choice<T>(string id, string label, T[] values, T current, Func<T, string> format, out T result)
    {
        Row(label);
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
        return changed;
    }

    private void DrawFooter(ApplicationSettings defaults, Func<ApplicationSettings, bool> tryApply,
        bool comboWasOpen, bool statusAbove, bool close)
    {
        ImGui.Separator();
        bool dirty = _draft != _original;
        string? status = _saveFailed ? Tr("保存失败，设置未应用", "Could not save settings") :
            dirty ? Tr("有未应用的更改", "Unsaved changes") : null;
        if (statusAbove && status is not null)
            ImGui.TextColored(Amber, status);

        float width = ImGui.GetContentRegionAvail().x;
        float left = ImGui.GetCursorPosX();
        float gap = ImGui.GetStyle()->ItemSpacing.x;
        float resetWidth = Math.Clamp((width - 2 * gap) / 3f, 1f, 118f);
        float buttonWidth = Math.Min(resetWidth, 90f);
        ImGui.BeginDisabled((byte)(_draft == defaults ? 1 : 0));
        if (ImGui.Button(Tr("恢复默认", "Reset defaults") + "###settings-reset", Vec(resetWidth, 0)) != 0)
            Change(defaults);
        ImGui.EndDisabled();
        if (!statusAbove && status is not null)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Amber, status);
        }
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), left + width - 2f * buttonWidth - gap));
        bool cancel = ImGui.Button(Tr("取消", "Cancel") + "###settings-cancel", Vec(buttonWidth, 0)) != 0;
        ImGui.SameLine();
        ImGui.PushStyleColor((int)ImGuiCol_Text, Color(0.05f, 0.10f, 0.18f));
        ImGui.PushStyleColor((int)ImGuiCol_Button, Accent);
        ImGui.PushStyleColor((int)ImGuiCol_ButtonHovered, Color(0.75f, 0.86f, 1f));
        ImGui.PushStyleColor((int)ImGuiCol_ButtonActive, Color(0.46f, 0.66f, 0.96f));
        ImGui.BeginDisabled((byte)(_draft == _original ? 1 : 0));
        bool apply = ImGui.Button(Tr("应用", "Apply") + "###settings-apply", Vec(buttonWidth, 0)) != 0;
        ImGui.EndDisabled();
        ImGui.PopStyleColor(4);

        bool escape = !comboWasOpen && ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_Escape, 0) != 0;
        if (apply)
            _saveFailed = !tryApply(_draft);
        if (close || cancel || escape || (apply && !_saveFailed))
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
        ImGui.PushStyleVar((int)ImGuiStyleVar_WindowPadding, Vec(18, 13));
        ImGui.PushStyleVar((int)ImGuiStyleVar_FramePadding, Vec(11, 6));
        ImGui.PushStyleVar((int)ImGuiStyleVar_ItemSpacing, Vec(9, 8));
        ImGui.PushStyleVar((int)ImGuiStyleVar_WindowRounding, 8f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_PopupRounding, 8f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_ChildRounding, 5f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_FrameRounding, 4f);
        ImGui.PushStyleVar((int)ImGuiStyleVar_GrabRounding, 3f);
    }

    private static ImVec2 Vec(float x, float y) => new() { x = x, y = y };
    private static ImVec4 Color(float r, float g, float b, float a = 1) => new() { x = r, y = g, z = b, w = a };
}
