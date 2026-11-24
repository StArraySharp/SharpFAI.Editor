using System.Globalization;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpFAI.Player.Engine.Filters;

/// <summary>Parsed ShaderLab defaults, reusable for each new material without retaining shader source.</summary>
internal sealed partial class ShaderLabDefaults
{
    private readonly struct Token(string text, bool quoted = false)
    {
        public string Text { get; } = text;
        public bool Quoted { get; } = quoted;
    }

    internal sealed class Property(string name, string type, float scalar = 0, int integer = 0,
        (float X, float Y, float Z, float W) vector = default, string? texture = null)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public float Scalar { get; } = scalar;
        public int Integer { get; } = integer;
        public (float X, float Y, float Z, float W) Vector { get; } = vector;
        public string? Texture { get; } = texture;
    }

    internal IReadOnlyList<Property> Properties { get; }

    internal ShaderLabDefaults(Property[] properties) => Properties = properties;

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
                result = new Property(name, type, scalar: scalar);
                break;
            case "Int":
            case "Integer":
                if (i >= tokens.Count || tokens[i].Quoted ||
                    !double.TryParse(tokens[i++].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) ||
                    double.IsNaN(integer) || integer < int.MinValue || integer > int.MaxValue ||
                    integer != Math.Truncate(integer)) return null;
                result = new Property(name, type, integer: (int)integer);
                break;
            case "Vector":
            case "Color":
                if (!Take(tokens, ref i, "(") || !Number(tokens, ref i, out float x) ||
                    !Take(tokens, ref i, ",") || !Number(tokens, ref i, out float y) ||
                    !Take(tokens, ref i, ",") || !Number(tokens, ref i, out float z) ||
                    !Take(tokens, ref i, ",") || !Number(tokens, ref i, out float w) ||
                    !Take(tokens, ref i, ")")) return null;
                result = new Property(name, type, vector: (x, y, z, w));
                break;
            case "2D":
                if (i >= tokens.Count || !tokens[i].Quoted) return null;
                result = new Property(name, type, texture: tokens[i++].Text);
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
            !float.IsNaN(value) && !float.IsInfinity(value);
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
            if ("(){}[],=".IndexOf(source[i]) >= 0)
            {
                tokens.Add(new Token(source[i++].ToString()));
                continue;
            }
            int start = i++;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) &&
                   "(){}[],=\"/".IndexOf(source[i]) < 0) i++;
            tokens.Add(new Token(source.Substring(start, i - start)));
        }
        return tokens;
    }
}
