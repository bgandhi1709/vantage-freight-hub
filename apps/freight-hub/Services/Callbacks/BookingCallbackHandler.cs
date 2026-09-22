using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Services.Callbacks;

/// <summary>
/// The forwarder confirming a booking — the point at which delivery events that arrived too early
/// become safe to act on.
/// </summary>
/// <remarks>
/// This handler does not replay those events itself; it reports the booking reference and the
/// dispatcher drains the queue. See <see cref="IForwarderCallbackHandler.HandleAsync"/> for why.
/// </remarks>
internal sealed class BookingCallbackHandler(
    IShipmentSnapshotRepository snapshots,
    ShipmentStateWriter writer,
    ILogger<BookingCallbackHandler> logger
) : IForwarderCallbackHandler
{
    public bool CanHandle(ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.EventType == WorkflowEventType.BookingCallback;

    public async Task<string?> HandleAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        var callback =
            workflowEvent.Callback
            ?? throw new InvalidOperationException("The booking callback carried no payload.");

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
                    ShipmentStatus.BookingFailed,
                    cancellationToken,
                    callback.Message
                )
                .ConfigureAwait(false);

            // A rejected booking unblocks nothing: a delivery event still has nothing to attach
            // to, so anything parked stays parked.
            return null;
        }

        await writer
            .WriteAsync(snapshot, ShipmentStatus.BookingConfirmed, cancellationToken)
            .ConfigureAwait(false);

        Log.CallbackRecorded(logger, callback.EventType, callback.ExternalId);

        return snapshot.BookingReference;
    }
}
