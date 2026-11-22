using System.Globalization;
using System.Resources;

namespace SharpFAI.Editor;

internal static class L
{
    private static readonly ResourceManager Strings = new("SharpFAI.Editor.Resources.EditorStrings", typeof(L).Assembly);

    public static CultureInfo Language { get; set; } = CultureInfo.CurrentUICulture;
    public static bool IsChinese => Language.TwoLetterISOLanguageName == "zh";
    private static CultureInfo Culture => CultureInfo.GetCultureInfo(IsChinese ? "zh-Hans" : "en");

    public static string T(string key) => Strings.GetString(key, Culture) ?? key;
    public static string F(string key, params object[] values) => string.Format(Culture, T(key), values);
    public static string Name(string identifier) => Strings.GetString("Name_" + identifier, Culture) ?? identifier;
    public static string Option(string identifier) => Strings.GetString("Option_" + identifier, Culture) ?? identifier;

    public static string LoadPhase(string phase) => phase switch
    {
        "启动中" => T("PhaseLoading"), "解析关卡" => T("PhaseParse"), "生成砖块" => T("PhaseFloors"),
        "计算时间轴" => T("PhaseTimeline"), "解码音频" => T("PhaseAudio"), "构建装饰与背景" => T("PhaseDecorations"),
        "构建可见砖块索引" => T("PhaseVisibility"), "烘焙网格" => T("PhaseBaking"), "上传 GPU" => T("PhaseUpload"),
        _ => phase
    };
}
