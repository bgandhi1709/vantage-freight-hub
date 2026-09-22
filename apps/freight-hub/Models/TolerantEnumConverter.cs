using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vantage.Freight.Hub.Models;

/// <summary>
/// Reads an enum by name and falls back to the zero value when the name is not one this build
/// knows, instead of throwing.
/// </summary>
/// <remarks>
/// Deliberate, and only for values that arrive from outside. A publisher that starts emitting an
/// event type this service has never heard of must not be able to make the very edge of the system
/// throw: a parse failure at the deserializer turns into a retry loop and then a dead-lettered
/// message, for an event that was never ours to handle. Degrading to the zero value lets the
/// dispatcher log the unknown type and complete the message, which is the documented behaviour.
///
/// Writing is always by name, so the representation stays stable across both stores.
/// </remarks>
internal sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
                ? parsed
                : default;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ordinal))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), ordinal);
            return Enum.IsDefined(candidate) ? candidate : default;
        }

        return default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
