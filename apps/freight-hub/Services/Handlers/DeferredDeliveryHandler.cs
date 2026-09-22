using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;

namespace Vantage.Freight.Hub.Services.Handlers;

/// <summary>
/// Catches a delivery event that arrived before its booking was confirmed, and keeps it.
/// </summary>
/// <remarks>
/// This is the ordering problem the whole design exists for. A driver marks a consignment
/// delivered while the forwarder has not yet answered the booking request, and the event is
/// perfectly valid — just early. Failing it would dead-letter a real delivery; dropping it would
/// lose one. So it is appended to the snapshot and replayed by
/// <see cref="Callbacks.BookingCallbackHandler"/> the moment the booking is confirmed.
///
/// Registration order matters: this handler sits immediately after
/// <see cref="DeliverySubmissionHandler"/>, whose guard is the exact inverse. Between them, every
/// delivery event is claimed by exactly one of the two.
/// </remarks>
internal sealed class DeferredDeliveryHandler(
    IShipmentSnapshotRepository snapshots,
    ILogger<DeferredDeliveryHandler> logger
) : IShipmentStateHandler
{
    public bool CanHandle(ShipmentSnapshot snapshot, ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.Command is UpdateShipmentStatusCommand { Status: "Delivered" }
        && snapshot.Status
            is not (
                ShipmentStatus.BookingConfirmed
                or ShipmentStatus.PickupConfirmed
                or ShipmentStatus.DeliveryFailed
            );

    public async Task HandleAsync(
        ShipmentSnapshot snapshot,
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        if (workflowEvent.Command is not UpdateShipmentStatusCommand command)
        {
            return;
        }

        snapshot.PendingStatusCommands.Add(command);
        await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);

        Log.EventParked(logger, snapshot.BookingReference);
    }
}
