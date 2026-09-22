using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Services.Callbacks;

/// <summary>The forwarder confirming the consignment was collected.</summary>
internal sealed class PickupCallbackHandler(
    IShipmentSnapshotRepository snapshots,
    ShipmentStateWriter writer,
    ILogger<PickupCallbackHandler> logger
) : IForwarderCallbackHandler
{
    public bool CanHandle(ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.EventType == WorkflowEventType.PickupCallback;

    public async Task<string?> HandleAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        var callback =
            workflowEvent.Callback
            ?? throw new InvalidOperationException("The pickup callback carried no payload.");

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
                    ShipmentStatus.PickupFailed,
                    cancellationToken,
                    callback.Message
                )
                .ConfigureAwait(false);
            return null;
        }

        snapshot.IsPickupConfirmed = true;
        await writer
            .WriteAsync(snapshot, ShipmentStatus.PickupConfirmed, cancellationToken)
            .ConfigureAwait(false);

        Log.CallbackRecorded(logger, callback.EventType, callback.ExternalId);

        return null;
    }
}
