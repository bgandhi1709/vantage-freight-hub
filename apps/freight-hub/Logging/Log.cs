using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Logging;

/// <summary>
/// Every log message this service can emit, declared once.
/// </summary>
/// <remarks>
/// Source-generated, so templates allocate nothing when the level is off and the full set of
/// events is readable in one file. Every message carries the correlation id, because the whole
/// point of an asynchronous lifecycle is that the interesting story is spread across several
/// messages minutes apart.
/// </remarks>
internal static partial class Log
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Dispatching {EventType} for {BookingReference}"
    )]
    internal static partial void Dispatching(
        ILogger logger,
        WorkflowEventType eventType,
        string bookingReference
    );

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Warning,
        Message = "No handler claimed {EventType} for {BookingReference}; completing the message"
    )]
    internal static partial void NoHandlerClaimedEvent(
        ILogger logger,
        WorkflowEventType eventType,
        string bookingReference
    );

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Operation type {OperationType} is not one this service handles; completing the message"
    )]
    internal static partial void UnknownOperationType(ILogger logger, OperationType operationType);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Information,
        Message = "Parked an event for {BookingReference}: no shipment is stored yet"
    )]
    internal static partial void EventParked(ILogger logger, string bookingReference);

    [LoggerMessage(
        EventId = 2004,
        Level = LogLevel.Information,
        Message = "Replaying {Count} parked event(s) for {BookingReference}"
    )]
    internal static partial void ReplayingParkedEvents(
        ILogger logger,
        int count,
        string bookingReference
    );

    [LoggerMessage(
        EventId = 2005,
        Level = LogLevel.Information,
        Message = "Shipment {BookingReference} moved to {Status}"
    )]
    internal static partial void StatusChanged(
        ILogger logger,
        string bookingReference,
        ShipmentStatus status
    );

    [LoggerMessage(
        EventId = 2006,
        Level = LogLevel.Error,
        Message = "The forwarder failed for {BookingReference} with status {StatusCode}"
    )]
    internal static partial void ForwarderFailed(
        ILogger logger,
        Exception exception,
        string bookingReference,
        int statusCode
    );

    [LoggerMessage(
        EventId = 2007,
        Level = LogLevel.Debug,
        Message = "[{CorrelationId}] outbound call: {Curl}"
    )]
    internal static partial void OutboundCall(ILogger logger, string correlationId, string curl);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Information,
        Message = "Callback {EventType} for forwarder reference {ForwarderReference} recorded"
    )]
    internal static partial void CallbackRecorded(
        ILogger logger,
        string eventType,
        string forwarderReference
    );

    [LoggerMessage(
        EventId = 2009,
        Level = LogLevel.Warning,
        Message = "Callback for forwarder reference {ForwarderReference} matched no shipment"
    )]
    internal static partial void CallbackUnmatched(ILogger logger, string forwarderReference);
}
