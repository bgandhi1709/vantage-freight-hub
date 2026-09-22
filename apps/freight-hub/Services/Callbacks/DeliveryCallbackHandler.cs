using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Services.Callbacks;

/// <summary>
/// The forwarder confirming delivery, which is also when the consignment becomes invoiceable.
/// </summary>
/// <remarks>
/// The invoice flag is set here rather than waiting for a separate event, because the forwarder
/// treats delivery as the billable moment. Marking it on the snapshot before anything downstream
/// reads it keeps a double-invoice off the table if this callback is redelivered.
/// </remarks>
internal sealed class DeliveryCallbackHandler(
    IShipmentSnapshotRepository snapshots,
    ShipmentStateWriter writer,
    ILogger<DeliveryCallbackHandler> logger
) : IForwarderCallbackHandler
{
    public bool CanHandle(ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.EventType == WorkflowEventType.DeliveryCallback;

    public async Task<string?> HandleAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        var callback =
            workflowEvent.Callback
            ?? throw new InvalidOperationException("The delivery callback carried no payload.");

        var snapshot = await snapshots
            .GetByForwarderReferenceAsync(callback.ExternalId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            Log.CallbackUnmatched(logger, callback.ExternalId);
            return null;
        }

        if (callback.IsError)
        {
            await writer
                .WriteAsync(
                    snapshot,
                    ShipmentStatus.DeliveryFailed,
                    cancellationToken,
                    callback.Message
                )
                .ConfigureAwait(false);
            return null;
        }

        if (snapshot.IsInvoiceSubmitted)
        {
            // Already billed. A redelivered callback must not produce a second invoice.
            return null;
        }

        snapshot.IsInvoiceSubmitted = true;
        await writer
            .WriteAsync(snapshot, ShipmentStatus.DeliveryConfirmed, cancellationToken)
            .ConfigureAwait(false);

        Log.CallbackRecorded(logger, callback.EventType, callback.ExternalId);

        return null;
    }
}
