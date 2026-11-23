using System.Globalization;
using System.Numerics;
using System.Text;

namespace SharpFAI.Player.Engine.Filters;

/// <summary>Parsed ShaderLab defaults, reusable for each new material without retaining shader source.</summary>
internal sealed class ShaderLabDefaults
{
    private readonly record struct Token(string Text, bool Quoted = false);
    private sealed record Property(string Name, string Type, float Scalar = 0, int Integer = 0,
        Vector4 Vector = default, string? Texture = null);

    private readonly Property[] properties;

    private ShaderLabDefaults(Property[] properties) => this.properties = properties;

    internal static void Apply(string shaderLab, IFilterMaterial material,
        Func<string, IFilterTexture?> builtInTexture) => Parse(shaderLab).Apply(material, builtInTexture);

    internal static ShaderLabDefaults Parse(string shaderLab)
    {
        var tokens = Tokenize(shaderLab);
        var properties = new List<Property>();
        int depth = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (Is(tokens, i, "Properties") && depth <= 1 && Is(tokens, i + 1, "{"))
            {
                i += 2;
                int nested = 0;
                for (; i < tokens.Count; i++)
                {
                    if (Is(tokens, i, "{") || Is(tokens, i, "[")) nested++;
                    else if (Is(tokens, i, "}") || Is(tokens, i, "]"))
                    {
                        if (nested == 0) break;
                        nested--;
                    }
                    else if (nested == 0 && !tokens[i].Quoted && Is(tokens, i + 1, "("))
                    {
                        int end = i;
                        var property = ReadProperty(tokens, ref end);
                        if (property != null)
                        {
                            if (property.Name != "_MainTex") properties.Add(property);
                            i = end - 1;
                        }
                    }
                }
                break;
            }
            if (Is(tokens, i, "{")) depth++;
            else if (Is(tokens, i, "}")) depth--;
        }
        return new ShaderLabDefaults(properties.ToArray());
    }

    internal void Apply(IFilterMaterial material, Func<string, IFilterTexture?> builtInTexture)
    {
        foreach (var property in properties)
        {
            switch (property.Type)
            {
                case "Float":
                case "Range": material.SetFloat(property.Name, property.Scalar); break;
                case "Int":
                case "Integer": material.SetInt(property.Name, property.Integer); break;
                case "Vector":
                case "Color": material.SetVector(property.Name, property.Vector); break;
                case "2D":
                    if (builtInTexture(property.Texture!) is { } texture)
                        material.SetTexture(property.Name, texture);
                    break;
            }
        }
    }

    private static Property? ReadProperty(List<Token> tokens, ref int position)
    {
        int i = position;
        string name = tokens[i++].Text;
        if (!Take(tokens, ref i, "(") || i >= tokens.Count || !tokens[i++].Quoted ||
            !Take(tokens, ref i, ",") || i >= tokens.Count) return null;
        var typeToken = tokens[i++];
        if (typeToken.Quoted) return null;
        string type = typeToken.Text;
        if (type == "Range" && (!Take(tokens, ref i, "(") || !Number(tokens, ref i, out _) ||
            !Take(tokens, ref i, ",") || !Number(tokens, ref i, out _) || !Take(tokens, ref i, ")")))
            return null;
        if (!Take(tokens, ref i, ")") || !Take(tokens, ref i, "=")) return null;
        Property result;
        switch (type)
        {
            case "Float":
            case "Range":
                if (!Number(tokens, ref i, out float scalar)) return null;
                result = new Property(name, type, Scalar: scalar);
                break;
            case "Int":
            case "Integer":
                if (i >= tokens.Count || tokens[i].Quoted ||
                    !double.TryParse(tokens[i++].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) ||
                    !double.IsFinite(integer) || integer < int.MinValue || integer > int.MaxValue ||
                    integer != Math.Truncate(integer)) return null;
                result = new Property(name, type, Integer: (int)integer);
                break;
            case "Vector":
            case "Color":
                if (!Take(tokens, ref i, "(") || !Number(tokens, ref i, out float x) ||
                    !Take(tokens, ref i, ",") || !Number(tokens, ref i, out float y) ||
                    !Take(tokens, ref i, ",") || !Number(tokens, ref i, out float z) ||
                    !Take(tokens, ref i, ",") || !Number(tokens, ref i, out float w) ||
                    !Take(tokens, ref i, ")")) return null;
                result = new Property(name, type, Vector: new Vector4(x, y, z, w));
                break;
            case "2D":
                if (i >= tokens.Count || !tokens[i].Quoted) return null;
                result = new Property(name, type, Texture: tokens[i++].Text);
                break;
            default: return null;
        }
        position = i;
        return result;
    }

    private static bool Number(List<Token> tokens, ref int position, out float value)
    {
        value = 0;
        return position < tokens.Count && !tokens[position].Quoted &&
            float.TryParse(tokens[position++].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            float.IsFinite(value);
    }

    private static bool Is(List<Token> tokens, int index, string text) =>
        index < tokens.Count && !tokens[index].Quoted && tokens[index].Text == text;

    private static bool Take(List<Token> tokens, ref int position, string text)
    {
        if (!Is(tokens, position, text)) return false;
        position++;
        return true;
    }

    private static List<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < source.Length;)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (source[i] == '/' && i + 1 < source.Length)
            {
                if (source[i + 1] == '/')
                {
                    i += 2;
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                if (source[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i = Math.Min(i + 2, source.Length);
                    continue;
                }
            }
            if (source[i] == '"')
            {
                i++;
                var text = new StringBuilder();
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length) i++;
                    text.Append(source[i++]);
                }
                if (i < source.Length) i++;
                tokens.Add(new Token(text.ToString(), true));
                continue;
            }
            if ("(){}[],=".Contains(source[i]))
            {
                tokens.Add(new Token(source[i++].ToString()));
                continue;
            }
            int start = i++;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) &&
                   !"(){}[],=\"/".Contains(source[i])) i++;
            tokens.Add(new Token(source[start..i]));
        }
        return tokens;
    }
}
