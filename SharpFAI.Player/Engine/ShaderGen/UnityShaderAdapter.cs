using System.Text;
using System.Text.RegularExpressions;

namespace SharpFAI.Player.Engine.ShaderGen;

/// <summary>
/// 把 Unity（hlslcc）导出的 GLSL 转换成引擎可以直接编译的顶点/片元着色器。
/// Converts GLSL exported by Unity (hlslcc) into vertex/fragment shaders the engine can compile.
/// </summary>
/// <remarks>
/// <para>
/// 语法清洗交给 <c>UGLSLAdapter</c>：拆 <c>#ifdef VERTEX</c>/<c>FRAGMENT</c>、求解 UNITY_* 兼容宏、
/// 展开 std140 uniform block、把 Unity 内置名换成 <c>u_*</c>。本类负责第二件事：把「Unity 逐
/// draw call 设置」的参数改成「逐顶点属性」，这样整关仍然只需要一次 draw call。
/// </para>
/// <para>
/// 官方输入 <c>in_POSITION0/in_NORMAL0/in_TEXCOORD0/in_TANGENT0/in_COLOR0</c> 与引擎
/// <c>VertexAttribute</c> 的 location 0..4 一一对应；逐实例参数从 location 5 开始按需分配。
/// </para>
/// </remarks>
public static partial class UnityShaderAdapter
{
    internal static string ImportFullscreenFragment(string original)
    {
        Match export = ExportedFragmentStage().Match(original);
        if (!export.Success)
            throw new InvalidDataException("Fragment stage missing from the Unity GLSL export.");

        // Unity's explicit uniform locations require OpenGL 4.3. Lookup by name on 4.1.
        return DisableExplicitUniformLocations(export.Groups[1].Value);
    }

    internal static string ImportExportedVertex(string original)
    {
        Match export = ExportedVertexStage().Match(original);
        if (!export.Success)
            throw new InvalidDataException("Vertex stage missing from the Unity GLSL export.");
        return DisableExplicitUniformLocations(export.Groups[1].Value);
    }

    private static string DisableExplicitUniformLocations(string source) =>
        source.Replace("#define UNITY_SUPPORTS_UNIFORM_LOCATION 1", "#define UNITY_SUPPORTS_UNIFORM_LOCATION 0",
            StringComparison.Ordinal);

    [GeneratedRegex(@"(?ms)^#ifdef[ \t]+FRAGMENT[ \t]*\r?\n(.*)\r?\n#endif[ \t]*\s*\z")]
    private static partial Regex ExportedFragmentStage();
    [GeneratedRegex(@"(?ms)^#ifdef[ \t]+VERTEX[ \t]*\r?\n(.*)\r?\n#endif[ \t]*\s*^#ifdef[ \t]+FRAGMENT")]
    private static partial Regex ExportedVertexStage();

    /// <summary>引擎已占用的顶点属性槽数（Position / Normal / TexCoord0 / Tangent / Color0）。</summary>
    private const int EngineBaseAttributeCount = 5;

    /// <summary>&gt;= 该版本才允许 <c>layout(location = ...)</c> 出现在顶点输入上。</summary>
    private const int ExplicitLocationVersion = 330;

    /// <summary>官方顶点输入名到引擎 location 的固定映射。</summary>
    private static readonly Dictionary<string, int> KnownAttributeLocations = new(StringComparer.Ordinal)
    {
        ["in_POSITION0"] = 0,
        ["in_NORMAL0"] = 1,
        ["in_TEXCOORD0"] = 2,
        ["in_TANGENT0"] = 3,
        ["in_COLOR0"] = 4,
    };

    /// <summary>官方顶点输入名到引擎属性名的映射，名字与 <c>Floor.vert</c> 保持一致。</summary>
    private static readonly Dictionary<string, string> AttributeToEngineName = new(StringComparer.Ordinal)
    {
        ["in_POSITION0"] = "aPosition",
        ["in_NORMAL0"] = "aNormal",
        ["in_TEXCOORD0"] = "aTexCoord",
        ["in_TANGENT0"] = "aTangent",
        ["in_COLOR0"] = "aVertexColor",
    };

    /// <summary>
    /// 未在 <see cref="KnownAttributeLocations"/> 里的官方输入按出现顺序追加到这些名字上。
    /// 引擎的 <c>Floor.vert</c> 用 location 7 放 uv2（瓷砖局部坐标），名字对齐即可。
    /// Official inputs not in <see cref="KnownAttributeLocations"/> take these names in order. The
    /// engine's <c>Floor.vert</c> keeps uv2 (tile-local coordinates) at location 7, so the names match.
    /// </summary>
    private static readonly string[] ExtraAttributeNames =
    [
        "aTileCoord",   // in_TEXCOORD1：瓷砖局部坐标
        "aColor1",      // in_COLOR1
        "aNormal1",     // in_NORMAL1
    ];

    /// <summary>UGLSLAdapter 重命名后的 Unity 内置 uniform 到引擎 uniform 名。</summary>
    private static readonly Dictionary<string, string> BuiltinToEngine = new(StringComparer.Ordinal)
    {
        ["u_ViewProjection"] = "uViewProjection",
        ["u_Model"] = "uModel",
        ["u_ScreenParams"] = "uScreenParams",
    };

    /// <summary>转换一个 hlslcc 单文件（<c>N_glsl.orig.glsl</c>）。</summary>
    /// <param name="source">原始 glsl 文本。</param>
    /// <param name="perInstanceUniforms">
    /// 需要改成逐顶点属性的 uniform 名（例如 <c>_UV2Offset</c>）。未列出的仍是全局 uniform。
    /// </param>
    public static AdapterResult Convert(string source, params string[] perInstanceUniforms)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<string> bridged = [.. perInstanceUniforms];

        // 第一步：语法清洗
        UGLSLAdapter.Hlslcc.ConvertedShader cleaned =
            UGLSLAdapter.Hlslcc.HlslccGlslConverter.Convert(source);

        // 第二步：引擎接入。属性槽要在两个阶段之间共享，所以先规划好再生成
        List<ShaderAttribute> attributes = PlanAttributes(
            cleaned.Vertex.Source,
            cleaned.Fragment.Source,
            bridged,
            out Dictionary<string, string> extraNameMap);

        StageResult vertex = AdaptVertexStage(
            cleaned.Vertex.Source,
            cleaned.Fragment.Source,
            attributes,
            extraNameMap,
            bridged);

        StageResult fragment = AdaptFragmentStage(cleaned.Fragment.Source, attributes, bridged);

        List<string> warnings =
            [.. cleaned.Warnings, .. vertex.Warnings, .. fragment.Warnings];

        return new AdapterResult(
            vertex.Source,
            fragment.Source,
            attributes,
            bridged,
            [.. warnings.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>转换结果。</summary>
    public sealed record AdapterResult(
        string VertexSource,
        string FragmentSource,
        IReadOnlyList<ShaderAttribute> Attributes,
        IReadOnlyList<string> BridgedUniforms,
        IReadOnlyList<string> Warnings);

    /// <summary>一个顶点属性及其在 shader 里的名字。</summary>
    public readonly record struct ShaderAttribute(int Location, string Name, string GlslType);

    private sealed record StageResult(string Source, List<string> Warnings);

    /// <summary>
    /// 规划顶点属性槽：官方输入用固定槽位，未映射的输入用预留名字，被桥接的 uniform 各占一个新槽。
    /// </summary>
    private static List<ShaderAttribute> PlanAttributes(
        string vertexSource,
        string fragmentSource,
        List<string> bridged,
        out Dictionary<string, string> extraNameMap)
    {
        List<ShaderAttribute> attributes = [];
        extraNameMap = new Dictionary<string, string>(StringComparer.Ordinal);

        int nextLocation = EngineBaseAttributeCount;
        int extraIndex = 0;

        foreach (string line in SplitLines(vertexSource))
        {
            if (!TryParseInput(line, out string? name, out string? glslType))
            {
                continue;
            }

            if (KnownAttributeLocations.TryGetValue(name!, out int known))
            {
                attributes.Add(new ShaderAttribute(known, AttributeToEngineName[name!], glslType!));
                continue;
            }

            // 未映射的输入：从预留名字里取一个，槽位紧接引擎基础槽
            string engineName = extraIndex < ExtraAttributeNames.Length
                ? ExtraAttributeNames[extraIndex]
                : "aExtra" + extraIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            extraIndex++;
            extraNameMap[name!] = engineName;
            attributes.Add(new ShaderAttribute(nextLocation++, engineName, glslType!));
        }

        foreach (string uniform in bridged)
        {
            if (!TryFindUniformType(fragmentSource, uniform, out string? glslType))
            {
                continue;
            }

            attributes.Add(new ShaderAttribute(
                nextLocation,
                AttributeNameFor(uniform),
                glslType!));

            nextLocation++;
        }

        return attributes;
    }

    private static StageResult AdaptVertexStage(
        string source,
        string fragmentSourceForTypes,
        List<ShaderAttribute> attributes,
        Dictionary<string, string> extraNameMap,
        List<string> bridged)
    {
        List<string> warnings = [];
        List<string> lines = MoveVersionToTop(SplitLines(source));

        // 官方输入换成引擎属性名，同时补显式 location
        for (int i = 0; i < lines.Count; i++)
        {
            if (!TryParseInput(lines[i], out string? name, out string? glslType))
            {
                continue;
            }

            if (KnownAttributeLocations.TryGetValue(name!, out int location))
            {
                lines[i] = $"layout(location = {location}) in {glslType} {AttributeToEngineName[name!]};";
                continue;
            }

            // 未映射的输入也用预留名字，槽位与 PlanAttributes 一致
            if (extraNameMap.TryGetValue(name!, out string? extraName))
            {
                int extraLocation = FindLocation(attributes, extraName);
                lines[i] = $"layout(location = {extraLocation}) in {glslType} {extraName};";
                continue;
            }

            warnings.Add($"未映射的顶点输入 {name} ({glslType})，已保留原名");
        }

        foreach ((string from, string to) in AttributeToEngineName)
        {
            ReplaceWord(lines, from, to);
        }

        foreach ((string from, string to) in extraNameMap)
        {
            ReplaceWord(lines, from, to);
        }

        // 桥接参数：删掉 uniform，改成同名属性并补 varying 输出。
        // 类型要从片元阶段取：这些 uniform 只在片元阶段真的被读取。
        // Bridged parameters: drop the uniform, add a like-named attribute and a matching varying.
        // The type comes from the fragment stage, where these uniforms are actually read.
        List<string> bridgedOutputs = [];

        foreach (string uniform in bridged)
        {
            if (!TryFindUniformType(fragmentSourceForTypes, uniform, out string? glslType))
            {
                continue;
            }

            int location = FindLocation(attributes, AttributeNameFor(uniform));
            string attributeName = AttributeNameFor(uniform);
            string varyingName = VaryingNameFor(uniform);

            RemoveUniform(lines, uniform);
            ReplaceWord(lines, uniform, attributeName);

            lines.Insert(
                FindInsertIndex(lines),
                $"layout(location = {location}) in {glslType} {attributeName};");

            bridgedOutputs.Add($"out {glslType} {varyingName};");
        }

        if (attributes.Count > 0)
        {
            lines = EnsureCoreVersion(lines, ExplicitLocationVersion);
        }

        foreach ((string from, string to) in BuiltinToEngine)
        {
            ReplaceWord(lines, from, to);
        }

        if (bridgedOutputs.Count > 0)
        {
            // varying 声明紧跟在最后的 out 声明之后
            int insertAt = lines.FindLastIndex(static l => l.TrimStart().StartsWith("out ", StringComparison.Ordinal));

            if (insertAt < 0)
            {
                insertAt = FindInsertIndex(lines) - 1;
            }

            lines.InsertRange(insertAt + 1, bridgedOutputs);
        }

        // 桥接属性写进同名 varying：在 main 开头补赋值
        if (bridged.Count > 0)
        {
            List<string> assignments = [];

            foreach (string uniform in bridged)
            {
                if (!TryFindUniformType(fragmentSourceForTypes, uniform, out _))
                {
                    continue;
                }

                string name = AttributeNameFor(uniform);
                assignments.Add($"    {VaryingNameFor(uniform)} = {name};");
            }

            if (assignments.Count > 0)
            {
                int mainIndex = lines.FindIndex(static l => l.Contains("void main()", StringComparison.Ordinal));

                if (mainIndex >= 0)
                {
                    lines.InsertRange(mainIndex + 2, assignments);
                }
            }
        }

        return new StageResult(string.Join('\n', lines), warnings);
    }

    private static StageResult AdaptFragmentStage(
        string source,
        List<ShaderAttribute> attributes,
        List<string> bridged)
    {
        List<string> warnings = [];
        List<string> lines = MoveVersionToTop(SplitLines(source));

        List<string> bridgedInputs = [];

        foreach (string uniform in bridged)
        {
            if (!TryFindUniformType(lines, uniform, out string? glslType))
            {
                continue;
            }

            RemoveUniform(lines, uniform);
            ReplaceWord(lines, uniform, VaryingNameFor(uniform));

            bridgedInputs.Add($"in {glslType} {VaryingNameFor(uniform)};");
        }

        if (bridgedInputs.Count > 0)
        {
            int insertAt = lines.FindLastIndex(static l => l.TrimStart().StartsWith("in ", StringComparison.Ordinal));

            if (insertAt < 0)
            {
                insertAt = FindInsertIndex(lines) - 1;
            }

            lines.InsertRange(insertAt + 1, bridgedInputs);
        }

        foreach ((string from, string to) in BuiltinToEngine)
        {
            ReplaceWord(lines, from, to);
        }

        // 顶点阶段用了显式 location，片元阶段的版本必须跟上来，否则链接时版本不一致
        // The vertex stage needs explicit locations, so lift the fragment version to match
        if (attributes.Count > 0)
        {
            lines = EnsureCoreVersion(lines, ExplicitLocationVersion);
        }

        return new StageResult(string.Join('\n', lines), warnings);
    }

    private static int FindLocation(List<ShaderAttribute> attributes, string name)
    {
        foreach (ShaderAttribute attribute in attributes)
        {
            if (string.Equals(attribute.Name, name, StringComparison.Ordinal))
            {
                return attribute.Location;
            }
        }

        return EngineBaseAttributeCount;
    }

    /// <summary>在第一条 <c>in</c>/<c>out</c>/<c>uniform</c> 之前插入声明的安全位置。</summary>
    private static int FindInsertIndex(List<string> lines)
    {
        int lastDeclaration = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();

            if (trimmed.StartsWith("in ", StringComparison.Ordinal)
                || trimmed.StartsWith("out ", StringComparison.Ordinal)
                || trimmed.StartsWith("uniform ", StringComparison.Ordinal)
                || trimmed.StartsWith("layout", StringComparison.Ordinal))
            {
                lastDeclaration = i;
            }

            if (trimmed.StartsWith("void main", StringComparison.Ordinal))
            {
                break;
            }
        }

        return lastDeclaration + 1;
    }

    private static List<string> SplitLines(string source)
        => [.. source.Replace("\r\n", "\n").Split('\n')];

    private static bool TryParseInput(string line, out string? name, out string? glslType)
    {
        name = null;
        glslType = null;

        Match match = InputDeclarationRegex().Match(line);

        if (!match.Success)
        {
            return false;
        }

        glslType = match.Groups["type"].Value;
        name = match.Groups["name"].Value;
        return true;
    }

    private static bool TryFindUniformType(string source, string uniform, out string? glslType)
        => TryFindUniformType(SplitLines(source), uniform, out glslType);

    private static bool TryFindUniformType(List<string> lines, string uniform, out string? glslType)
    {
        glslType = null;
        Regex regex = new(
            $@"^\s*uniform\s+(?<type>\w+)\s+{Regex.Escape(uniform)}\s*;\s*$",
            RegexOptions.CultureInvariant);

        foreach (string line in lines)
        {
            Match match = regex.Match(line);

            if (!match.Success)
            {
                continue;
            }

            glslType = match.Groups["type"].Value;
            return true;
        }

        return false;
    }

    private static void RemoveUniform(List<string> lines, string uniform)
    {
        Regex regex = new(
            $@"^\s*uniform\s+\w+\s+{Regex.Escape(uniform)}\s*;\s*$",
            RegexOptions.CultureInvariant);

        lines.RemoveAll(line => regex.IsMatch(line));
    }

    private static List<string> EnsureCoreVersion(List<string> lines, int minimum)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            Match match = VersionRegex().Match(lines[i]);

            if (!match.Success)
            {
                continue;
            }

            int current = int.Parse(
                match.Groups["version"].Value,
                System.Globalization.CultureInfo.InvariantCulture);

            if (current >= minimum && lines[i].Contains("core", StringComparison.Ordinal))
            {
                return lines;
            }

            lines[i] = $"#version {Math.Max(current, minimum)} core";
            return lines;
        }

        return lines;
    }

    private static List<string> MoveVersionToTop(List<string> lines)
    {
        int index = lines.FindIndex(
            static l => l.TrimStart().StartsWith("#version", StringComparison.Ordinal));

        if (index <= 0)
        {
            return lines;
        }

        string version = lines[index];
        lines.RemoveAt(index);
        lines.Insert(0, version);
        return lines;
    }

    /// <summary>按整词替换标识符，避免 <c>_Color</c> 误伤 <c>_ShadowColor</c>。</summary>
    private static void ReplaceWord(List<string> lines, string from, string to)
    {
        if (string.IsNullOrEmpty(from))
        {
            return;
        }

        Regex regex = new(
            $@"(?<![\w.]){Regex.Escape(from)}(?![\w])",
            RegexOptions.CultureInvariant);

        for (int i = 0; i < lines.Count; i++)
        {
            lines[i] = regex.Replace(lines[i], to);
        }
    }

    /// <summary>
    /// 把 Unity 的 uniform 名拆成词元：<c>_UV2Offset</c> → <c>UV2</c> + <c>Offset</c>。
    /// Splits a Unity uniform name into words: <c>_UV2Offset</c> becomes <c>UV2</c> + <c>Offset</c>.
    /// </summary>
    private static List<string> SplitWords(string value)
    {
        List<string> words = [];
        StringBuilder current = new();

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (c == '_')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            bool isUpper = char.IsUpper(c);
            bool isDigit = char.IsDigit(c);
            bool previousIsUpper = current.Length > 0 && char.IsUpper(current[^1]);
            bool previousIsDigit = current.Length > 0 && char.IsDigit(current[^1]);

            // 新词开始的三种情形：
            //   1. 数字后接字母（UV2 + Offset）
            //   2. 连续大写之后的「大写+小写」转折点（UV + Offset）
            bool nextIsLower = i + 1 < value.Length && char.IsLower(value[i + 1]);

            if (current.Length > 0)
            {
                if (previousIsDigit && !isDigit)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                else if (isUpper && !previousIsDigit && (!previousIsUpper || nextIsLower))
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>词元拼成 camelCase：<c>_UV2Offset</c> → <c>uv2Offset</c>。</summary>
    private static string ToCamelCase(string value)
    {
        List<string> words = SplitWords(value);

        if (words.Count == 0)
        {
            return value;
        }

        StringBuilder builder = new(value.Length);

        // 首词整体小写（含其后的数字），其余词首字母大写
        builder.Append(words[0].ToLowerInvariant());

        for (int i = 1; i < words.Count; i++)
        {
            string word = words[i];
            builder.Append(char.ToUpperInvariant(word[0]));
            builder.Append(word[1..].ToLowerInvariant());
        }

        return builder.ToString();
    }

    /// <summary>生成引擎侧属性名，例如 <c>_UV2Offset</c> 变成 <c>aUv2Offset</c>。</summary>
    private static string AttributeNameFor(string uniform) => "a" + Capitalise(ToCamelCase(uniform));

    /// <summary>生成 varying 名，例如 <c>_UV2Offset</c> 变成 <c>vUv2Offset</c>。</summary>
    private static string VaryingNameFor(string uniform) => "v" + Capitalise(ToCamelCase(uniform));

    private static string Capitalise(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex(@"^\s*in\s+(?<type>\w+)\s+(?<name>in_\w+)\s*;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InputDeclarationRegex();

    [GeneratedRegex(@"^#version\s+(?<version>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
