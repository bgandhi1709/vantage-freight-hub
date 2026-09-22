using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;

namespace Vantage.Freight.Hub.Services.Handlers;

/// <summary>
/// Starts a consignment: make sure the forwarder knows the consignee, then book the freight.
/// </summary>
/// <remarks>
/// The account has to exist before a booking can reference it, and creating it is asynchronous —
/// the forwarder answers by callback. So this handler either books straight away (account already
/// confirmed) or writes <see cref="ShipmentStatus.AccountCreationPending"/> and stops, leaving the
/// account callback to replay it.
///
/// The pending state is written to both stores <em>before</em> the outbound call, and a failure
/// writes <see cref="ShipmentStatus.AccountCreationFailed"/> and rethrows. Writing after the call
/// would leave a request in flight that no stored state accounts for, which on a retry becomes a
/// second account for one consignee.
/// </remarks>
internal sealed class SubmitShipmentHandler(
    IForwarderClient forwarder,
    IShipmentSnapshotRepository snapshots,
    IShipmentLifecycleTable lifecycle,
    ShipmentStateWriter writer,
    BookingSubmissionStep bookingStep,
    ILogger<SubmitShipmentHandler> logger
) : IShipmentStateHandler
{
    public bool CanHandle(ShipmentSnapshot snapshot, ShipmentWorkflowEvent workflowEvent) =>
        workflowEvent.Command?.OperationType == OperationType.SubmitShipment;

    public async Task HandleAsync(
        ShipmentSnapshot snapshot,
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        var command =
            workflowEvent.Command as SubmitShipmentCommand
            ?? throw new InvalidOperationException("The submit event carried no submit command.");

        snapshot.SubmitCommand = command;
        snapshot.ConsigneeId = command.ConsigneeId;
        snapshot.SourceSystem = command.SourceSystem;

        if (snapshot.IsAccountConfirmed)
        {
            await bookingStep
                .SubmitAsync(snapshot, workflowEvent.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Another shipment for the same consignee may already have asked for the account. Asking
        // twice would create two accounts for one consignee, which the forwarder does not dedupe.
        var alreadyRequested = await lifecycle
            .GetByStatusAsync(
                command.SourceSystem,
                command.ConsigneeId,
                ShipmentStatus.AccountCreationPending,
                cancellationToken
            )
            .ConfigureAwait(false);

        await writer
            .WriteAsync(snapshot, ShipmentStatus.AccountCreationPending, cancellationToken)
            .ConfigureAwait(false);

        if (alreadyRequested.Count > 0)
        {
            return;
        }

        try
        {
            var ack = await forwarder
                .CreateConsigneeAccountAsync(command, workflowEvent.CorrelationId, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(ack.ForwarderReference))
            {
                snapshot.ForwarderReference = ack.ForwarderReference;
                await snapshots
                    .SaveForwarderIndexAsync(
                        ack.ForwarderReference,
                        snapshot.SourceSystem,
                        snapshot.BookingReference,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (ForwarderException ex)
        {
            Log.ForwarderFailed(logger, ex, snapshot.BookingReference, ex.StatusCode);

            await writer
                .WriteAsync(
                    snapshot,
                    ShipmentStatus.AccountCreationFailed,
                    cancellationToken,
                    ex.Message
                )
                .ConfigureAwait(false);

            // Rethrow so the bus retries with its own policy. Swallowing here would lose the event.
            throw;
        }
    }
}
