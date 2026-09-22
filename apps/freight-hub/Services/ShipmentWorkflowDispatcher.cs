using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Services;

public interface IShipmentWorkflowDispatcher
{
    Task DispatchAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Routes an event to the first handler whose guard claims it.
/// </summary>
/// <remarks>
/// Registration order is priority order, and it is declared in Program.cs with a comment saying so.
/// The chain replaced what would otherwise be a switch on event type: the decision this service
/// actually makes depends on the shipment's stored state as much as on the event, and the guards
/// are where that comparison belongs. Adding a lifecycle step becomes one class and one
/// registration line, instead of another arm on a switch that already has too many.
///
/// An event nothing claims is logged and completed. So is an operation type this build does not
/// know. Neither is an error: the queue carries events for more than this service, and failing on
/// something that was never ours would dead-letter perfectly good messages.
/// </remarks>
internal sealed class ShipmentWorkflowDispatcher(
    IEnumerable<IShipmentStateHandler> stateHandlers,
    IEnumerable<IForwarderCallbackHandler> callbackHandlers,
    IShipmentSnapshotRepository snapshots,
    ICommandEnvelopeFactory envelopeFactory,
    ILogger<ShipmentWorkflowDispatcher> logger
) : IShipmentWorkflowDispatcher
{
    public async Task DispatchAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(workflowEvent);

        if (workflowEvent.EventType != WorkflowEventType.ShipmentCommand)
        {
            await this.DispatchCallbackAsync(workflowEvent, cancellationToken).ConfigureAwait(false);
            return;
        }

        await this.DispatchCommandAsync(workflowEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchCallbackAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken
    )
    {
        foreach (var handler in callbackHandlers)
        {
            if (handler.CanHandle(workflowEvent))
            {
                var unblocked = await handler
                    .HandleAsync(workflowEvent, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(unblocked))
                {
                    await this.ReplayParkedAsync(
                            unblocked,
                            workflowEvent.CorrelationId,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                return;
            }
        }

        Log.NoHandlerClaimedEvent(logger, workflowEvent.EventType, string.Empty);
    }

    /// <summary>
    /// Drains the events parked for a shipment that has just been unblocked.
    /// </summary>
    /// <remarks>
    /// Parking without draining is the half-finished version of this design: the shipment simply
    /// stops progressing, and nothing fails, so nobody notices. Two queues are drained — commands
    /// appended to the snapshot by the deferred-delivery handler, and raw bodies parked here for a
    /// shipment that did not exist yet.
    ///
    /// Both are cleared before anything is replayed. A replayed event that throws is retried by
    /// the bus from the original message, and a queue that still held it would replay it twice.
    /// </remarks>
    private async Task ReplayParkedAsync(
        string bookingReference,
        string correlationId,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await snapshots
            .GetAsync("bookings", bookingReference, cancellationToken)
            .ConfigureAwait(false);

        var parkedCommands = snapshot?.PendingStatusCommands.ToList() ?? [];
        var parkedBodies = await snapshots
            .GetPendingAsync(bookingReference, cancellationToken)
            .ConfigureAwait(false);

        if (parkedCommands.Count == 0 && parkedBodies.Count == 0)
        {
            return;
        }

        Log.ReplayingParkedEvents(
            logger,
            parkedCommands.Count + parkedBodies.Count,
            bookingReference
        );

        if (snapshot is not null && parkedCommands.Count > 0)
        {
            snapshot.PendingStatusCommands.Clear();
            await snapshots.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        await snapshots.ClearPendingAsync(bookingReference, cancellationToken).ConfigureAwait(false);

        foreach (var command in parkedCommands)
        {
            await this.DispatchAsync(
                    new ShipmentWorkflowEvent
                    {
                        EventType = WorkflowEventType.ShipmentCommand,
                        CorrelationId = correlationId,
                        Command = command,
                        RawBody = string.Empty,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        foreach (var body in parkedBodies)
        {
            var envelope = envelopeFactory.Create(body);
            var command = envelope.Deserialize<UpdateShipmentStatusCommand>();

            if (command is null)
            {
                continue;
            }

            await this.DispatchAsync(
                    new ShipmentWorkflowEvent
                    {
                        EventType = WorkflowEventType.ShipmentCommand,
                        CorrelationId = correlationId,
                        Command = command,
                        RawBody = body,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private async Task DispatchCommandAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken
    )
    {
        var command = workflowEvent.Command;

        if (command is null || command.OperationType == OperationType.Unknown)
        {
            Log.UnknownOperationType(logger, command?.OperationType ?? OperationType.Unknown);
            return;
        }

        var reference = ExtractReference(command);
        var sourceSystem = ExtractSourceSystem(command);

        Log.Dispatching(logger, workflowEvent.EventType, reference);

        var snapshot = await snapshots
            .GetAsync(sourceSystem, reference, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            if (command.OperationType != OperationType.SubmitShipment)
            {
                // A status event for a shipment this service has never seen. Park it: the submit
                // it belongs to may simply be a few seconds behind on another partition.
                await snapshots
                    .SavePendingAsync(reference, workflowEvent.RawBody, cancellationToken)
                    .ConfigureAwait(false);
                Log.EventParked(logger, reference);
                return;
            }

            snapshot = new ShipmentSnapshot
            {
                BookingReference = reference,
                SourceSystem = sourceSystem,
            };
        }

        foreach (var handler in stateHandlers)
        {
            if (handler.CanHandle(snapshot, workflowEvent))
            {
                await handler
                    .HandleAsync(snapshot, workflowEvent, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        Log.NoHandlerClaimedEvent(logger, workflowEvent.EventType, reference);
    }

    private static string ExtractReference(CommandBase command) =>
        command switch
        {
            SubmitShipmentCommand submit => ReferenceNormalizer.NormalizeBookingReference(
                submit.BookingReference
            ),
            UpdateShipmentStatusCommand update => ReferenceNormalizer.NormalizeBookingReference(
                update.BookingReference
            ),
            _ => string.Empty,
        };

    private static string ExtractSourceSystem(CommandBase command) =>
        command switch
        {
            SubmitShipmentCommand submit => submit.SourceSystem,
            UpdateShipmentStatusCommand update => update.SourceSystem,
            _ => "bookings",
        };
}
