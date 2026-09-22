using System.Text.Json.Serialization;

namespace Vantage.Freight.Hub.Models;

/// <summary>
/// What kind of command a bus message carries. Read on the first deserialization pass, before the
/// payload is typed.
/// </summary>
[JsonConverter(typeof(TolerantEnumConverter<OperationType>))]
public enum OperationType
{
    Unknown = 0,
    SubmitShipment = 1,
    UpdateShipmentStatus = 2,
}

/// <summary>
/// The shape every inbound command shares. The first deserialization pass reads only this, which
/// is how the dispatcher decides what the second, typed pass should produce.
/// </summary>
public class CommandBase
{
    [JsonPropertyName("operationType")]
    public OperationType OperationType { get; set; }
}

public sealed class SubmitShipmentCommand : CommandBase
{
    [JsonPropertyName("bookingReference")]
    public string BookingReference { get; set; } = string.Empty;

    [JsonPropertyName("consigneeId")]
    public string ConsigneeId { get; set; } = string.Empty;

    [JsonPropertyName("consigneeName")]
    public string ConsigneeName { get; set; } = string.Empty;

    [JsonPropertyName("venue")]
    public string Venue { get; set; } = string.Empty;

    [JsonPropertyName("collectionDate")]
    public DateTimeOffset CollectionDate { get; set; }

    [JsonPropertyName("pieces")]
    public int Pieces { get; set; }

    [JsonPropertyName("weightKg")]
    public decimal WeightKg { get; set; }

    /// <summary>Which booking system raised this. Part of the storage path and the table partition.</summary>
    [JsonPropertyName("sourceSystem")]
    public string SourceSystem { get; set; } = "bookings";
}

/// <summary>
/// A later status event for a shipment already submitted — collected, delivered, cancelled.
/// </summary>
public sealed class UpdateShipmentStatusCommand : CommandBase
{
    [JsonPropertyName("bookingReference")]
    public string BookingReference { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("occurredUtc")]
    public DateTimeOffset OccurredUtc { get; set; }

    [JsonPropertyName("sourceSystem")]
    public string SourceSystem { get; set; } = "bookings";
}
