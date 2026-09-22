using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Services.Handlers;

/// <summary>
/// Cancels a booking the forwarder has already been told about. A consignment that was never
/// submitted needs no cancellation, which is what the guard checks.
/// </summary>
internal sealed class CancellationHandler(IForwarderClient forwarder, ShipmentStateWriter writer)
    : IShipmentStateHandler
{
    public bool CanHandle(ShipmentSnapshot snapshot, ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.Command is UpdateShipmentStatusCommand { Status: "Cancelled" }
        && snapshot.IsBookingSubmitted
        && snapshot.Status != ShipmentStatus.Cancelled;

    public async Task HandleAsync(
        ShipmentSnapshot snapshot,
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        await forwarder
            .CancelBookingAsync(
                snapshot.BookingReference,
                snapshot.ForwarderReference,
                workflowEvent.CorrelationId,
                cancellationToken
            )
            .ConfigureAwait(false);

        await writer
            .WriteAsync(snapshot, ShipmentStatus.Cancelled, cancellationToken)
            .ConfigureAwait(false);
    }
}
