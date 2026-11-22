using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Editor;

// Bind nested controls to their owning event so undo and Player invalidation use the event identity.
internal sealed record EditorPropertyEdit(EditorDocument Document, JObject Owner, string[] Path)
{
    public static EditorPropertyEdit Capture(EditorDocument document, JObject target, string key)
    {
        var path = new List<string> { key };
        JToken node = target;
        while (!ReferenceEquals(node, document.Settings) &&
               (node is not JObject obj || obj["eventType"] is null) && node.Parent is not null)
        {
            if (node.Parent is JProperty property)
            {
                path.Insert(0, property.Name);
                node = property.Parent!;
            }
            else if (node.Parent is JArray array)
            {
                path.Insert(0, array.IndexOf(node).ToString(CultureInfo.InvariantCulture));
                node = array;
            }
            else break;
        }
        return new(document, node as JObject ?? target, path.ToArray());
    }

    public bool Matches(EditorPropertyEdit other) => ReferenceEquals(Document, other.Document) &&
        ReferenceEquals(Owner, other.Owner) && Path.SequenceEqual(other.Path);

    public JToken? Read()
    {
        JToken? value = Owner;
        foreach (string part in Path)
            value = value is JArray array ? array.ElementAtOrDefault(int.Parse(part, CultureInfo.InvariantCulture)) : value?[part];
        return value;
    }

    public void Set(JToken? value, object? group = null)
    {
        if (Owner.Parent is null && !ReferenceEquals(Owner, Document.Settings)) return;
        if (Path.Length == 1)
        {
            Document.Set(Owner, Path[0], value, group);
            return;
        }
        JToken? root = Owner[Path[0]]?.DeepClone();
        if (root is null) return;
        JToken parent = root;
        foreach (string part in Path.Skip(1).SkipLast(1))
            parent = parent is JArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : parent[part]!;
        string last = Path[^1];
        if (parent is JArray list)
            list[int.Parse(last, CultureInfo.InvariantCulture)] = value?.DeepClone() ?? JValue.CreateNull();
        else if (value is null)
            ((JObject)parent).Remove(last);
        else parent[last] = value.DeepClone();
        Document.Set(Owner, Path[0], root, group);
    }

    public static double ClampNumericDrag(EditorField field, double value)
    {
        bool whole = field.Type is "Int" or "Tile";
        double min = field.Minimum ?? (whole ? int.MinValue : double.MinValue);
        double max = field.Maximum ?? (whole ? int.MaxValue : double.MaxValue);
        if (whole)
        {
            min = Math.Max(int.MinValue, Math.Ceiling(min));
            max = Math.Min(int.MaxValue, Math.Floor(max));
            value = Math.Round(value);
        }
        return min <= max ? Math.Clamp(value, min, max) : double.NaN;
    }

    public static bool TryNumber(EditorField field, string text, out JToken value)
    {
        value = JValue.CreateNull();
        if (string.IsNullOrWhiteSpace(text) && field.AllowEmpty) return true;
        bool whole = field.Type is "Int" or "Tile";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
            !double.IsFinite(number) || number < (field.Minimum ?? double.NegativeInfinity) ||
            number > (field.Maximum ?? double.PositiveInfinity) ||
            whole && (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)) return false;
        value = whole ? new JValue((int)number) : new JValue(number);
        return true;
    }

    public static JToken WithComponent(JToken? current, EditorField field, int component, JToken value)
    {
        if (component < 0) return value;
        var result = current is JArray array ? (JArray)array.DeepClone() : new JArray();
        JArray destination = result;
        int index = component;
        if (field.Type == "Vector2Range")
        {
            index = component / 2;
            while (result.Count <= index) result.Add(new JArray());
            if (result[index] is not JArray inner) result[index] = inner = new JArray();
            destination = inner;
            index = component % 2;
        }
        while (destination.Count <= index) destination.Add(JValue.CreateNull());
        destination[index] = value;
        if (result.Count == 1) result.Add(field.Type == "Tile" ? new JValue("ThisTile") : JValue.CreateNull());
        return result;
    }

    public static bool TryColor(string? text, out System.Numerics.Vector4 color)
    {
        color = System.Numerics.Vector4.One;
        string hex = (text ?? "").TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
            return false;
        if (hex.Length == 6) raw = raw << 8 | 255;
        color = new((raw >> 24) / 255f, (raw >> 16 & 255) / 255f, (raw >> 8 & 255) / 255f, (raw & 255) / 255f);
        return true;
    }

    public static string ColorHex(System.Numerics.Vector4 color) => string.Concat(new[] { color.X, color.Y, color.Z, color.W }
        .Select(value => ((byte)MathF.Round(Math.Clamp(value, 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture)));
}
