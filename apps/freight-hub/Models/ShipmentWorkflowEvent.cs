using System.Text.Json.Serialization;

namespace Vantage.Freight.Hub.Models;

[JsonConverter(typeof(TolerantEnumConverter<WorkflowEventType>))]
public enum WorkflowEventType
{
    Unknown = 0,

    /// <summary>A command from the booking system: submit, or a later status change.</summary>
    ShipmentCommand = 1,

    /// <summary>The forwarder confirming it created the consignee account.</summary>
    AccountCallback = 2,

    /// <summary>The forwarder confirming the booking.</summary>
    BookingCallback = 3,

    /// <summary>The forwarder confirming collection.</summary>
    PickupCallback = 4,

    /// <summary>The forwarder confirming delivery.</summary>
    DeliveryCallback = 5,
}

/// <summary>
/// One envelope for everything the workflow handles, whichever queue or topic it arrived on.
/// </summary>
/// <remarks>
/// Commands and callbacks arrive on different subscriptions and look nothing alike, but they drive
/// the same state machine. Wrapping both in one envelope means the dispatcher has a single entry
/// point and the handlers are chosen by what they can handle, not by where the message came from.
/// </remarks>
public sealed class ShipmentWorkflowEvent
{
    public required WorkflowEventType EventType { get; init; }

    /// <summary>
    /// The bus message id, carried through so every log line written while handling this event can
    /// be tied back to the message that caused it.
    /// </summary>
    public required string CorrelationId { get; init; }

    /// <summary>Set for command events; null for callbacks.</summary>
    public CommandBase? Command { get; init; }

    /// <summary>Set for callback events; null for commands.</summary>
    public ForwarderCallback? Callback { get; init; }

    /// <summary>The raw message body, kept for the audit record and the typed second pass.</summary>
    public required string RawBody { get; init; }
}
