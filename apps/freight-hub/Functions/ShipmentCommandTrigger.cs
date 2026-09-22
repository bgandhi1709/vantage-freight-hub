using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Functions;

/// <summary>
/// Booking system events. The trigger deserializes, wraps and dispatches — nothing else.
/// </summary>
/// <remarks>
/// Deliberately thin. Every decision lives behind <see cref="IShipmentWorkflowDispatcher"/>, which
/// is what lets the same lifecycle be driven from the bus in production and from an HTTP call in
/// a local run without the two paths diverging.
///
/// Exceptions are not caught. Service Bus already has a retry and dead-letter policy; catching here
/// would replace a mechanism that works with one that has to be maintained.
/// </remarks>
internal sealed class ShipmentCommandTrigger(
    ICommandEnvelopeFactory envelopeFactory,
    IShipmentWorkflowDispatcher dispatcher,
    ILogger<ShipmentCommandTrigger> logger
)
{
    [Function("ShipmentCommands")]
    public async Task RunAsync(
        [ServiceBusTrigger("%ShipmentQueueName%", Connection = "ServiceBusConnection")]
            ServiceBusReceivedMessage message,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = message.Body.ToString();
        var envelope = envelopeFactory.Create(body);

        // The bus message id is the correlation id, so every log line written while handling this
        // message — including the forwarder call it triggers — ties back to one message.
        using var scope = logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = message.MessageId }
        );

        var command = envelope.Command.OperationType switch
        {
            OperationType.SubmitShipment => (CommandBase?)
                envelope.Deserialize<SubmitShipmentCommand>(),
            OperationType.UpdateShipmentStatus => envelope.Deserialize<UpdateShipmentStatusCommand>(),
            _ => envelope.Command,
        };

        await dispatcher
            .DispatchAsync(
                new ShipmentWorkflowEvent
                {
                    EventType = WorkflowEventType.ShipmentCommand,
                    CorrelationId = message.MessageId,
                    Command = command,
                    RawBody = body,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
