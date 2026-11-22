using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SharpFAI.Player.Engine.Events;

using SharpFAI.Serialization;

/// <summary>Two optional axes. A scalar input sets both axes.</summary>
[JsonConverter(typeof(EventVectorConverter))]
internal readonly struct EventVector
{
    public float? X { get; }
    public float? Y { get; }

    public EventVector(float? x, float? y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>A floor index relative to the event, start, or end of the chart.</summary>
[JsonConverter(typeof(TileReferenceConverter))]
internal readonly struct TileReference
{
    public int Offset { get; }
    public EventEnums.TileRelativeTo RelativeTo { get; }

    public TileReference(int offset, EventEnums.TileRelativeTo relativeTo)
    {
        Offset = offset;
        RelativeTo = relativeTo;
    }

    public int Resolve(int eventFloor, int lastFloor)
    {
        int origin = RelativeTo switch
        {
            EventEnums.TileRelativeTo.Start => 0,
            EventEnums.TileRelativeTo.End => lastFloor,
            _ => eventFloor,
        };
        long index = (long)origin + Offset;
        return (int)Math.Min(lastFloor, Math.Max(0, index));
    }
}

internal sealed class EventVectorConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(EventVector)
        || objectType == typeof(EventVector?);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        JToken value = JToken.Load(reader);
        if (value.Type == JTokenType.Null)
        {
            return null;
        }

        if (value is JArray { Count: >= 2 } pair)
        {
            return new EventVector(ReadAxis(pair[0]), ReadAxis(pair[1]));
        }

        float? scalar = ReadAxis(value);
        return new EventVector(scalar, scalar);
    }

    private static float? ReadAxis(JToken value) => value is JValue scalar
        && float.TryParse(Convert.ToString(scalar.Value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float axis) && !float.IsNaN(axis) && !float.IsInfinity(axis) ? axis : null;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is EventVector vector)
        {
            writer.WriteStartArray();
            writer.WriteValue(vector.X);
            writer.WriteValue(vector.Y);
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull();
        }
    }
}

internal sealed class TileReferenceConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(TileReference)
        || objectType == typeof(TileReference?);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        JToken value = JToken.Load(reader);
        if (value is JArray { Count: >= 2 } pair)
        {
            return Parse(pair[0].ToString(), pair[1].ToString());
        }

        if (value.Type == JTokenType.String)
        {
            string[] tuple = value.ToString().Trim('(', ')').Split(',');
            if (tuple.Length == 2)
            {
                return Parse(tuple[0], tuple[1]);
            }
        }

        return null;
    }

    private static TileReference Parse(string offset, string relative) => new(
        int.TryParse(offset, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : 0,
        Enum.TryParse(relative.Trim(), true, out EventEnums.TileRelativeTo kind) ? kind : EventEnums.TileRelativeTo.ThisTile);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is TileReference reference)
        {
            writer.WriteStartArray();
            writer.WriteValue(reference.Offset);
            writer.WriteValue(reference.RelativeTo.ToString());
            writer.WriteEndArray();
        }
        else
        {
            writer.WriteNull();
        }
    }
}
