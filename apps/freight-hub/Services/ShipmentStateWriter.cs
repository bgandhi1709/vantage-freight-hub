using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;

namespace Vantage.Freight.Hub.Services;

/// <summary>
/// The one place a shipment's state is written.
/// </summary>
/// <remarks>
/// Both stores are written together, snapshot first. The snapshot is canonical; the table is an
/// index derived from it. Writing the index first would let it claim a state the snapshot cannot
/// confirm — which reads, to anyone querying, as a shipment that reached a step it never reached.
/// Every handler goes through here so that ordering is decided once.
/// </remarks>
internal sealed class ShipmentStateWriter(
    IShipmentSnapshotRepository snapshots,
    IShipmentLifecycleTable lifecycle,
    TimeProvider clock,
    ILogger<ShipmentStateWriter> logger
)
{
    internal async Task WriteAsync(
        ShipmentSnapshot snapshot,
        ShipmentStatus status,
        CancellationToken cancellationToken,
        string failureMessage = ""
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        snapshot.Status = status;
        snapshot.LastFailureMessage = failureMessage;
        snapshot.LastUpdatedUtc = clock.GetUtcNow();

        await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);

        await lifecycle
            .UpsertAsync(
                new ShipmentLifecycleRecord
                {
                    SourceSystem = snapshot.SourceSystem,
                    BookingReference = snapshot.BookingReference,
                    ForwarderReference = snapshot.ForwarderReference,
                    ConsigneeId = snapshot.ConsigneeId,
                    Status = status,
                    LastFailureMessage = failureMessage,
                    LastUpdatedUtc = snapshot.LastUpdatedUtc,
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        Log.StatusChanged(logger, snapshot.BookingReference, status);
    }
}
