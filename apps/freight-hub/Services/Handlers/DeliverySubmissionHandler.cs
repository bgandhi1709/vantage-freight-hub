using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Services.Handlers;

/// <summary>
/// Tells the forwarder a consignment has been delivered — but only once the booking it belongs to
/// has actually been confirmed.
/// </summary>
internal sealed class DeliverySubmissionHandler(
    IForwarderClient forwarder,
    ShipmentStateWriter writer
) : IShipmentStateHandler
{
    public bool CanHandle(ShipmentSnapshot snapshot, ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.Command is UpdateShipmentStatusCommand { Status: "Delivered" }
        && snapshot.Status
            is ShipmentStatus.BookingConfirmed
                or ShipmentStatus.PickupConfirmed
                or ShipmentStatus.DeliveryFailed
        && !snapshot.IsDeliverySubmitted;

    public async Task HandleAsync(
        ShipmentSnapshot snapshot,
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await forwarder
                .SubmitDeliveryAsync(
                    snapshot.BookingReference,
                    snapshot.ForwarderReference,
                    workflowEvent.CorrelationId,
                    cancellationToken
                )
                .ConfigureAwait(false);

            snapshot.IsDeliverySubmitted = true;
            await writer
                .WriteAsync(snapshot, ShipmentStatus.DeliverySubmitted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ForwarderException ex)
        {
            await writer
                .WriteAsync(snapshot, ShipmentStatus.DeliveryFailed, cancellationToken, ex.Message)
                .ConfigureAwait(false);
            throw;
        }
    }
}
