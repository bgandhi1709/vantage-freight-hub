using System.Text.Json.Serialization;

namespace Vantage.Freight.Hub.Models;

/// <summary>
/// Everything known about one consignment: the command that started it, what has been sent to the
/// forwarder, what the forwarder has confirmed, and anything that arrived too early to act on.
/// </summary>
/// <remarks>
/// This is the canonical record. The lifecycle table is an index built from it and can be rebuilt;
/// this cannot. The boolean flags are idempotency guards, not status: a handler asks "have I
/// already sent this?" rather than inferring it from a status that several paths can set.
/// </remarks>
public sealed class ShipmentSnapshot
{
    /// <summary>Normalized booking reference. Also the blob name and the table row key.</summary>
    [JsonPropertyName("bookingReference")]
    public string BookingReference { get; set; } = string.Empty;

    /// <summary>
    /// The forwarder's own identifier, which is all its callbacks carry. Resolving it back to a
    /// booking reference is what the secondary index blob exists for.
    /// </summary>
    [JsonPropertyName("forwarderReference")]
    public string ForwarderReference { get; set; } = string.Empty;

    [JsonPropertyName("sourceSystem")]
    public string SourceSystem { get; set; } = string.Empty;

    [JsonPropertyName("consigneeId")]
    public string ConsigneeId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public ShipmentStatus Status { get; set; } = ShipmentStatus.Unknown;

    [JsonPropertyName("submitCommand")]
    public SubmitShipmentCommand? SubmitCommand { get; set; }

    [JsonPropertyName("isAccountConfirmed")]
    public bool IsAccountConfirmed { get; set; }

    [JsonPropertyName("isBookingSubmitted")]
    public bool IsBookingSubmitted { get; set; }

    [JsonPropertyName("isPickupConfirmed")]
    public bool IsPickupConfirmed { get; set; }

    [JsonPropertyName("isDeliverySubmitted")]
    public bool IsDeliverySubmitted { get; set; }

    [JsonPropertyName("isInvoiceSubmitted")]
    public bool IsInvoiceSubmitted { get; set; }

    [JsonPropertyName("lastFailureMessage")]
    public string LastFailureMessage { get; set; } = string.Empty;

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTimeOffset LastUpdatedUtc { get; set; }

    /// <summary>
    /// Status events that arrived before the shipment could act on them — a delivery confirmation
    /// for a booking the forwarder has not confirmed yet, most often. They are kept and replayed
    /// rather than dropped or dead-lettered.
    /// </summary>
    [JsonPropertyName("pendingStatusCommands")]
    public List<UpdateShipmentStatusCommand> PendingStatusCommands { get; set; } = [];
}
