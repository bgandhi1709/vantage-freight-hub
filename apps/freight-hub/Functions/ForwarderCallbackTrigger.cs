using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Functions;

/// <summary>
/// Forwarder callbacks. One subscription per event type, all mapping onto the same envelope.
/// </summary>
internal sealed class ForwarderCallbackTrigger(
    IShipmentWorkflowDispatcher dispatcher,
    JsonSerializerOptions serializerOptions,
    ILogger<ForwarderCallbackTrigger> logger
)
{
    [Function("AccountCallbacks")]
    public Task RunAccountAsync(
        [ServiceBusTrigger(
            "%CallbackTopicName%",
            "%AccountSubscriptionName%",
            Connection = "ServiceBusConnection"
        )]
            ServiceBusReceivedMessage message,
        CancellationToken cancellationToken
    ) => this.DispatchAsync(message, WorkflowEventType.AccountCallback, cancellationToken);

    [Function("BookingCallbacks")]
    public Task RunBookingAsync(
        [ServiceBusTrigger(
            "%CallbackTopicName%",
            "%BookingSubscriptionName%",
            Connection = "ServiceBusConnection"
        )]
            ServiceBusReceivedMessage message,
        CancellationToken cancellationToken
    ) => this.DispatchAsync(message, WorkflowEventType.BookingCallback, cancellationToken);

    [Function("PickupCallbacks")]
    public Task RunPickupAsync(
        [ServiceBusTrigger(
            "%CallbackTopicName%",
            "%PickupSubscriptionName%",
            Connection = "ServiceBusConnection"
        )]
            ServiceBusReceivedMessage message,
        CancellationToken cancellationToken
    ) => this.DispatchAsync(message, WorkflowEventType.PickupCallback, cancellationToken);

    [Function("DeliveryCallbacks")]
    public Task RunDeliveryAsync(
        [ServiceBusTrigger(
            "%CallbackTopicName%",
            "%DeliverySubscriptionName%",
            Connection = "ServiceBusConnection"
        )]
            ServiceBusReceivedMessage message,
        CancellationToken cancellationToken
    ) => this.DispatchAsync(message, WorkflowEventType.DeliveryCallback, cancellationToken);

    private async Task DispatchAsync(
        ServiceBusReceivedMessage message,
        WorkflowEventType eventType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = message.Body.ToString();

        using var scope = logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = message.MessageId }
        );

        await dispatcher
            .DispatchAsync(
                new ShipmentWorkflowEvent
                {
                    EventType = eventType,
                    CorrelationId = message.MessageId,
                    Callback = JsonSerializer.Deserialize<ForwarderCallback>(
                        body,
                        serializerOptions
                    ),
                    RawBody = body,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }
}
