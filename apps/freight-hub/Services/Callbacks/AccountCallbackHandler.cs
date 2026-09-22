using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Services.Callbacks;

/// <summary>
/// The forwarder confirming it created a consignee account — which unblocks every shipment that
/// was parked waiting for it.
/// </summary>
/// <remarks>
/// The fan-out is the point: several shipments for one consignee can be sitting in
/// AccountCreationPending, and one callback releases all of them. Each is booked in turn, and a
/// failure on one does not abandon the rest.
/// </remarks>
internal sealed class AccountCallbackHandler(
    IShipmentSnapshotRepository snapshots,
    IShipmentLifecycleTable lifecycle,
    ShipmentStateWriter writer,
    BookingSubmissionStep bookingStep,
    ILogger<AccountCallbackHandler> logger
) : IForwarderCallbackHandler
{
    public bool CanHandle(ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.EventType == WorkflowEventType.AccountCallback;

    public async Task<string?> HandleAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        var callback =
            workflowEvent.Callback
            ?? throw new InvalidOperationException("The account callback carried no payload.");

        var snapshot = await snapshots
            .GetByForwarderReferenceAsync(callback.ExternalId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            // Recorded rather than thrown: an unmatched callback is visible in the log and does not
            // cost the forwarder a retry it cannot resolve.
            Log.CallbackUnmatched(logger, callback.ExternalId);
            return null;
        }

        if (callback.IsError)
        {
            await writer
                .WriteAsync(
                    snapshot,
                    ShipmentStatus.AccountCreationFailed,
                    cancellationToken,
                    callback.Message
                )
                .ConfigureAwait(false);
            return null;
        }

        snapshot.IsAccountConfirmed = true;
        await writer
            .WriteAsync(snapshot, ShipmentStatus.AccountCreated, cancellationToken)
            .ConfigureAwait(false);

        // This shipment first. It is the one that asked for the account, and its own lifecycle row
        // has just moved out of AccountCreationPending — so the fan-out below will not find it.
        await bookingStep
            .SubmitAsync(snapshot, workflowEvent.CorrelationId, cancellationToken)
            .ConfigureAwait(false);

        var waiting = await lifecycle
            .GetByStatusAsync(
                snapshot.SourceSystem,
                snapshot.ConsigneeId,
                ShipmentStatus.AccountCreationPending,
                cancellationToken
            )
            .ConfigureAwait(false);

        foreach (var record in waiting)
        {
            if (
                string.Equals(
                    record.BookingReference,
                    snapshot.BookingReference,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var parked = await snapshots
                .GetAsync(record.SourceSystem, record.BookingReference, cancellationToken)
                .ConfigureAwait(false);

            if (parked is null)
            {
                continue;
            }

            parked.IsAccountConfirmed = true;
            await bookingStep
                .SubmitAsync(parked, workflowEvent.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
        }

        Log.CallbackRecorded(logger, callback.EventType, callback.ExternalId);

        return null;
    }
}
