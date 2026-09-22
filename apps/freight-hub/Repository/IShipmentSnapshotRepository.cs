using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Repository;

/// <summary>
/// Raised when a callback or status event names a consignment this service has never stored.
/// Used for control flow: the dispatcher catches it and parks the event for replay.
/// </summary>
public sealed class ShipmentNotFoundException(string reference)
    : Exception($"No shipment is stored for '{reference}'.")
{
    public string Reference { get; } = reference;
}

/// <summary>
/// The canonical store: one JSON snapshot per consignment, plus the index that resolves the
/// forwarder's own reference back to ours.
/// </summary>
public interface IShipmentSnapshotRepository
{
    Task SaveAsync(ShipmentSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<ShipmentSnapshot?> GetAsync(
        string sourceSystem,
        string bookingReference,
        CancellationToken cancellationToken = default
    );

    /// <summary>Resolves a forwarder reference through the index blob, then loads the snapshot.</summary>
    Task<ShipmentSnapshot?> GetByForwarderReferenceAsync(
        string forwarderReference,
        CancellationToken cancellationToken = default
    );

    /// <summary>Records the mapping a callback will later arrive with.</summary>
    Task SaveForwarderIndexAsync(
        string forwarderReference,
        string sourceSystem,
        string bookingReference,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Stores an event that arrived before its shipment existed, under a separate prefix so it is
    /// visibly pending rather than masquerading as a snapshot.
    /// </summary>
    Task SavePendingAsync(
        string bookingReference,
        string rawBody,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<string>> GetPendingAsync(
        string bookingReference,
        CancellationToken cancellationToken = default
    );

    Task ClearPendingAsync(
        string bookingReference,
        CancellationToken cancellationToken = default
    );
}
