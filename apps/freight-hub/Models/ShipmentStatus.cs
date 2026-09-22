namespace Vantage.Freight.Hub.Models;

/// <summary>
/// Where a consignment has reached in the forwarder's lifecycle.
/// </summary>
/// <remarks>
/// Members are appended, never reordered or renumbered. The value is written by name to both the
/// snapshot blob and the lifecycle table (see the serializer configuration), and snapshots written
/// months ago are still read back; an ordinal that quietly changes meaning is the failure this
/// rule exists to prevent.
/// </remarks>
public enum ShipmentStatus
{
    Unknown = 0,
    AccountCreationPending = 1,
    AccountCreationFailed = 2,
    AccountCreated = 3,
    BookingSubmitted = 4,
    BookingFailed = 5,
    BookingConfirmed = 6,
    PickupConfirmed = 7,
    PickupFailed = 8,
    DeliverySubmitted = 9,
    DeliveryFailed = 10,
    DeliveryConfirmed = 11,
    Cancelled = 12,
    InvoiceProcessed = 13,
}
