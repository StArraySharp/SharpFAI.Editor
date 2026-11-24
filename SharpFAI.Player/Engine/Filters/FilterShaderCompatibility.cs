using System.Text.RegularExpressions;

namespace SharpFAI.Player.Engine.Filters;

internal static class FilterShaderCompatibility
{
    internal const string HlslccDxbcHeader = "// SharpFAI HLSLcc DXBC\n";

    internal static string AdaptFragment(string shaderName, string source, bool translatedDxbc = false)
    {
        if (string.Equals(shaderName, "CameraFilterPack_TV_VHS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(shaderName, "CameraFilterPack/TV_VHS", StringComparison.OrdinalIgnoreCase))
            return AdaptVhs(source, translatedDxbc);

        if (!string.Equals(shaderName, "CameraFilterPack_Oculus_ThermaVision", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(shaderName, "CameraFilterPack/Oculus_ThermaVision", StringComparison.OrdinalIgnoreCase))
            return source;

        var code = MaskCommentsAndDirectives(source);
        var depths = new int[code.Length];
        int depth = 0;
        for (int i = 0; i < code.Length; i++)
        {
            depths[i] = depth;
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth < 0) throw Incompatible();
        }
        if (depth != 0) throw Incompatible();

        Match[] Find(string pattern) => Regex.Matches(code, pattern, RegexOptions.CultureInvariant)
            .Cast<Match>().Where(m => depths[m.Index] == 0).ToArray();

        var uniforms = Find(@"\buniform\s+float\s+_Value\s*;");
        var outputs = Find(@"\bout\s+(?:lowp\s+|mediump\s+|highp\s+)?\w+\s+\w+\s*(?:\[[^\]]*\]\s*)?;");
        var main = Find(@"\bvoid\s+main\s*\(\s*(?:void\s*)?\)\s*\{");
        if (uniforms.Length != 1 || outputs.Length != 1 || main.Length != 1 ||
            uniforms[0].Index > main[0].Index || outputs[0].Index > main[0].Index)
            throw Incompatible();

        var output = Regex.Match(outputs[0].Value,
            @"^out\s+(?:lowp\s+|mediump\s+|highp\s+)?vec4\s+(?<name>[A-Za-z_]\w*)\s*;$",
            RegexOptions.CultureInvariant);
        if (!output.Success) throw Incompatible();

        // Singular compatibility: Unity's UNORM output is black at zero, but GLSL may
        // fold Inf * 0 into a finite red channel. Guard before division; leave all
        // nonzero arithmetic and other shaders untouched instead of changing division globally.
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string guard = newline + "    if (_Value == 0.0) { " + output.Groups["name"].Value +
            " = vec4(0.0); return; }" + newline;
        return source.Insert(main[0].Index + main[0].Length, guard);
    }

    private static string AdaptVhs(string source, bool translatedDxbc)
    {
        string code = MaskCommentsAndDirectives(source);
        var main = Regex.Matches(code, @"\bvoid\s+main\s*\(\s*(?:void\s*)?\)\s*\{", RegexOptions.CultureInvariant);
        if (main.Count != 1)
            throw new InvalidDataException("TV_VHS compatibility requires one exported main body.");
        int start = main[0].Index + main[0].Length;
        int end = start, depth = 1;
        while (end < code.Length && depth != 0)
        {
            if (code[end] == '{') depth++;
            else if (code[end] == '}') depth--;
            end++;
        }
        if (depth != 0)
            throw new InvalidDataException("TV_VHS compatibility requires a complete exported main body.");

        // D3D's base UV is top-down; our input and textures are bottom-up. Keep the
        // noise hashes in D3D UVs. Its pre-roll flip cancels that conversion, and
        // its post-roll sample flip cancels the texture conversion. The parasite
        // hash still uses pre-flip roll Y, but its threshold uses post-flip Y.
        (string Before, string After)[] changes = translatedDxbc
            ?
            [
                ("vs_TEXCOORD0.xy * _MainTex_ST.xy", "vec2(vs_TEXCOORD0.x, 1.0 - vs_TEXCOORD0.y) * _MainTex_ST.xy"),
                ("texture(_MainTex, u_xlat3.xy)", "texture(_MainTex, vec2(u_xlat3.x, 1.0 - u_xlat3.y))")
            ]
            :
        [
            ("u_xlat1.x = u_xlat2.y;", "u_xlat2.y = 1.0 - u_xlat2.y; u_xlat1.x = u_xlat2.y;"),
            ("u_xlat5 = _TimeX * _Value3 + u_xlat2.y;", "u_xlat5 = _TimeX * _Value3 + (1.0 - u_xlat2.y);"),
            ("u_xlat10.xy = (-u_xlat3.yy) * vec2(30.0, 5.0) + vec2(11.5, 1.5);",
                "u_xlat10.xy = (u_xlat3.yy - vec2(1.0)) * vec2(30.0, 5.0) + vec2(11.5, 1.5);")
        ];
        var replacements = changes.Select(change =>
        {
            string pattern = Regex.Escape(change.Before).Replace(@"\ ", @"\s+", StringComparison.Ordinal);
            var matches = Regex.Matches(code, pattern, RegexOptions.CultureInvariant);
            if (matches.Count != 1 || matches[0].Index < start || matches[0].Index + matches[0].Length >= end)
                throw new InvalidDataException("TV_VHS compatibility requires the original exported coordinate expressions.");
            return (Match: matches[0], change.After);
        }).ToArray();
        if (!replacements.Select(item => item.Match.Index).SequenceEqual(replacements.Select(item => item.Match.Index).Order()))
            throw new InvalidDataException("TV_VHS compatibility requires the original coordinate expression order.");
        foreach (var (match, after) in replacements.Reverse())
            source = source.Remove(match.Index, match.Length).Insert(match.Index, after);
        return source;
    }

    private static InvalidDataException Incompatible() =>
        new("ThermaVision fragment compatibility requires one uniform float _Value, one vec4 output, and one exported main body.");

    private static string MaskCommentsAndDirectives(string source)
    {
        char[] code = source.ToCharArray();
        for (int i = 0; i < source.Length;)
        {
            int start = i;
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw Incompatible();
                i = end + 2;
            }
            else if (source[i] == '#')
            {
                do
                {
                    int end = source.IndexOf('\n', i);
                    if (end < 0) { i = source.Length; break; }
                    int last = end - 1;
                    if (last >= i && source[last] == '\r') last--;
                    i = end + 1;
                    if (last < start || source[last] != '\\') break;
                } while (i < source.Length);
            }
            else { i++; continue; }

            for (int j = start; j < i; j++)
                if (code[j] != '\r' && code[j] != '\n') code[j] = ' ';
        }
        return new string(code);
    }
}
