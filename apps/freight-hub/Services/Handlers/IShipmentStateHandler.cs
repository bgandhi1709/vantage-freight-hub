using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Services.Handlers;

/// <summary>
/// Handles a command from the booking system, for a shipment in a particular state.
/// </summary>
/// <remarks>
/// The guard takes the snapshot as well as the event, and that is the whole design. "Delivered"
/// means one thing when the forwarder has confirmed the booking and something entirely different
/// when it has not, and only the snapshot knows which. A switch on the event type cannot express
/// that; two handlers with different guards can.
/// </remarks>
public interface IShipmentStateHandler
{
    bool CanHandle(ShipmentSnapshot snapshot, ShipmentWorkflowEvent workflowEvent);

    Task HandleAsync(
        ShipmentSnapshot snapshot,
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Handles a confirmation the forwarder sends back out of band.</summary>
public interface IForwarderCallbackHandler
{
    bool CanHandle(ShipmentWorkflowEvent workflowEvent);

    /// <summary>
    /// Handles the callback and returns the booking reference whose parked events are now safe to
    /// replay, or null when nothing was unblocked.
    /// </summary>
    /// <remarks>
    /// A handler reports what it unblocked rather than replaying events itself. Replay means
    /// re-entering dispatch, and a handler that called the dispatcher back would be a cycle — the
    /// container refuses to build it, and the design would be wrong anyway: the dispatcher parks
    /// these events, so the dispatcher drains them.
    /// </remarks>
    Task<string?> HandleAsync(
        ShipmentWorkflowEvent workflowEvent,
        CancellationToken cancellationToken = default
    );
}
