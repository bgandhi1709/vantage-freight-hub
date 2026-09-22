using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vantage.Freight.Hub.Functions;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Tests.Functions;

/// <summary>
/// The trigger is tested against real <see cref="ServiceBusReceivedMessage"/> instances rather than
/// a hand-made stand-in, so the binding shape the platform actually delivers is what gets exercised.
/// </summary>
public sealed class ShipmentCommandTriggerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly Mock<IShipmentWorkflowDispatcher> dispatcher = new();

    private ShipmentCommandTrigger CreateTrigger() =>
        new(
            new CommandEnvelopeFactory(Options),
            this.dispatcher.Object,
            NullLogger<ShipmentCommandTrigger>.Instance
        );

    [Fact]
    public async Task RunAsync_TypesASubmitCommandOnTheSecondPass()
    {
        ShipmentWorkflowEvent? dispatched = null;
        this.CaptureDispatched(e => dispatched = e);

        await this.CreateTrigger()
            .RunAsync(
                NewMessage(
                    """
                    {"operationType":"SubmitShipment","bookingReference":"VW-1042-7",
                     "consigneeId":"acme-expo","pieces":4}
                    """
                ),
                CancellationToken.None
            );

        var command = Assert.IsType<SubmitShipmentCommand>(dispatched?.Command);
        Assert.Equal("VW-1042-7", command.BookingReference);
        Assert.Equal(4, command.Pieces);
    }

    [Fact]
    public async Task RunAsync_TypesAStatusCommandOnTheSecondPass()
    {
        ShipmentWorkflowEvent? dispatched = null;
        this.CaptureDispatched(e => dispatched = e);

        await this.CreateTrigger()
            .RunAsync(
                NewMessage(
                    """
                    {"operationType":"UpdateShipmentStatus","bookingReference":"VW-1042-7","status":"Delivered"}
                    """
                ),
                CancellationToken.None
            );

        var command = Assert.IsType<UpdateShipmentStatusCommand>(dispatched?.Command);
        Assert.Equal("Delivered", command.Status);
    }

    [Fact]
    public async Task RunAsync_CarriesTheBusMessageIdAsTheCorrelationId()
    {
        // Every later log line, and the header on the outbound forwarder call, trace back to this.
        ShipmentWorkflowEvent? dispatched = null;
        this.CaptureDispatched(e => dispatched = e);

        await this.CreateTrigger()
            .RunAsync(
                NewMessage("""{"operationType":"SubmitShipment"}""", messageId: "msg-4711"),
                CancellationToken.None
            );

        Assert.Equal("msg-4711", dispatched?.CorrelationId);
    }

    [Fact]
    public async Task RunAsync_PassesTheRawBodyThroughUnchanged()
    {
        var body = """{"operationType":"SubmitShipment","fieldWeDoNotModel":123}""";
        ShipmentWorkflowEvent? dispatched = null;
        this.CaptureDispatched(e => dispatched = e);

        await this.CreateTrigger().RunAsync(NewMessage(body), CancellationToken.None);

        Assert.Equal(body, dispatched?.RawBody);
    }

    [Fact]
    public async Task RunAsync_WithAnUnknownOperationType_StillDispatchesForTheDispatcherToDecide()
    {
        // Deciding what to do with an unrecognised event belongs to the dispatcher, not the edge.
        ShipmentWorkflowEvent? dispatched = null;
        this.CaptureDispatched(e => dispatched = e);

        await this.CreateTrigger()
            .RunAsync(
                NewMessage("""{"operationType":"SomethingNobodyModelled"}"""),
                CancellationToken.None
            );

        Assert.Equal(OperationType.Unknown, dispatched?.Command?.OperationType);
    }

    [Fact]
    public async Task RunAsync_WhenTheDispatcherThrows_LetsTheBusSeeIt()
    {
        // No catch in the trigger: Service Bus retry and dead-lettering is the recovery mechanism.
        this.dispatcher
            .Setup(d =>
                d.DispatchAsync(It.IsAny<ShipmentWorkflowEvent>(), It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new InvalidOperationException("downstream failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                this.CreateTrigger()
                    .RunAsync(
                        NewMessage("""{"operationType":"SubmitShipment"}"""),
                        CancellationToken.None
                    )
        );
    }

    private void CaptureDispatched(Action<ShipmentWorkflowEvent> capture) =>
        this.dispatcher
            .Setup(d =>
                d.DispatchAsync(It.IsAny<ShipmentWorkflowEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback((ShipmentWorkflowEvent e, CancellationToken _) => capture(e))
            .Returns(Task.CompletedTask);

    private static ServiceBusReceivedMessage NewMessage(string body, string messageId = "msg-1") =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(body),
            messageId: messageId
        );
}
