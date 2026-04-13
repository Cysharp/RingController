using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingController;

/// <summary>
/// Unknown or removed <c>kind</c> values in JSON deserialize as <see cref="RingActionKind.None"/> instead of throwing.
/// </summary>
public sealed class RingActionKindJsonConverter : JsonConverter<RingActionKind>
{
    static readonly JsonNamingPolicy Camel = JsonNamingPolicy.CamelCase;

    public override RingActionKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
            {
                var s = reader.GetString();
                if (string.IsNullOrEmpty(s))
                    return RingActionKind.None;
                foreach (RingActionKind v in Enum.GetValues<RingActionKind>())
                {
                    var name = v.ToString();
                    if (string.Equals(Camel.ConvertName(name), s, StringComparison.OrdinalIgnoreCase))
                        return v;
                }
                return RingActionKind.None;
            }
            case JsonTokenType.Number:
            {
                if (!reader.TryGetInt32(out var n))
                    return RingActionKind.None;
                if (Enum.IsDefined(typeof(RingActionKind), n))
                    return (RingActionKind)n;
                return RingActionKind.None;
            }
            default:
                return RingActionKind.None;
        }
    }

    public override void Write(Utf8JsonWriter writer, RingActionKind value, JsonSerializerOptions options)
    {
        var name = value.ToString();
        writer.WriteStringValue(Camel.ConvertName(name));
    }
}
