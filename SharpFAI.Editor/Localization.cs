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
    public static string Name(string identifier, string? owner = null) => Label("Name_", identifier, owner);
    public static string Option(string identifier) => Label("Option_", identifier);

    public static string Option(string identifier, EditorField field)
    {
        var options = EditorCatalog.Options(field);
        if (options is null || !options.Contains(identifier, StringComparer.Ordinal))
            return identifier;
        return Label("Option_", identifier, EditorCatalog.EnumName(field.Type));
    }

    private static string Label(string prefix, string identifier, string? scope = null)
    {
        CultureInfo culture = Culture;
        return (scope is null ? null : Strings.GetString($"{prefix}{scope}_{identifier}", culture)) ??
            Strings.GetString(prefix + identifier, culture) ?? identifier;
    }

    public static string LoadPhase(string phase) => phase switch
    {
        "准备渲染资源" => T("PhaseUpload"),
        "启动中" => T("PhaseLoading"), "解析关卡" => T("PhaseParse"), "生成砖块" => T("PhaseFloors"),
        "计算时间轴" => T("PhaseTimeline"), "解码音频" => T("PhaseAudio"), "构建装饰与背景" => T("PhaseDecorations"),
        "构建可见砖块索引" => T("PhaseVisibility"), "烘焙网格" => T("PhaseBaking"), "上传 GPU" => T("PhaseUpload"),
        "解码轨道纹理" => T("PhaseTrackTextures"),
        _ => phase
    };
}
