using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vantage.Freight.Hub.Models;

/// <summary>
/// What the forwarder posts back when an operation finishes, minutes or hours after it was asked
/// for. It identifies the consignment by its own reference, not by ours.
/// </summary>
public sealed class ForwarderCallback
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>The forwarder's identifier. Resolved back to a booking reference via the index blob.</summary>
    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>
    /// Arrives as a boolean from some endpoints and as the string "true"/"false" from others.
    /// The converter below accepts both rather than letting one endpoint's habit break the parse.
    /// </summary>
    [JsonPropertyName("isError")]
    [JsonConverter(typeof(FlexibleBooleanConverter))]
    public bool IsError { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("occurredUtc")]
    public DateTimeOffset OccurredUtc { get; set; }
}

/// <summary>Reads a boolean that may have been serialized as a boolean or as a string.</summary>
internal sealed class FlexibleBooleanConverter : JsonConverter<bool>
{
    public override bool Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String => bool.TryParse(reader.GetString(), out var parsed) && parsed,
            JsonTokenType.Number => reader.GetInt32() != 0,
            _ => false,
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
