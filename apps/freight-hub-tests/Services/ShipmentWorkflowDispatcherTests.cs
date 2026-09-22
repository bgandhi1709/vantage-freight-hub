using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Tests.Services;

public sealed class ShipmentWorkflowDispatcherTests
{
    private readonly Mock<IShipmentSnapshotRepository> snapshots = new();

    [Fact]
    public async Task DispatchAsync_PicksTheFirstHandlerWhoseGuardClaimsTheEvent()
    {
        // Registration order is priority order. A later handler that also matches must not run.
        var first = NewStateHandler(canHandle: true);
        var second = NewStateHandler(canHandle: true);
        this.GivenSnapshotExists(NewSnapshot());

        await this.CreateDispatcher([first.Object, second.Object])
            .DispatchAsync(NewStatusEvent("Delivered"));

        first.Verify(
            handler =>
                handler.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        second.Verify(
            handler =>
                handler.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task DispatchAsync_SkipsAHandlerWhoseGuardDeclines()
    {
        var declining = NewStateHandler(canHandle: false);
        var accepting = NewStateHandler(canHandle: true);
        this.GivenSnapshotExists(NewSnapshot());

        await this.CreateDispatcher([declining.Object, accepting.Object])
            .DispatchAsync(NewStatusEvent("Delivered"));

        accepting.Verify(
            handler =>
                handler.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task DispatchAsync_GivesTheHandlerTheStoredSnapshotNotAFreshOne()
    {
        // The guards compare the event against stored state, so handing them an empty snapshot
        // would make every guard decide on half the information.
        var stored = NewSnapshot();
        stored.Status = ShipmentStatus.BookingConfirmed;
        stored.IsBookingSubmitted = true;
        this.GivenSnapshotExists(stored);

        ShipmentSnapshot? seen = null;
        var handler = NewStateHandler(canHandle: true);
        handler
            .Setup(h =>
                h.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (ShipmentSnapshot snapshot, ShipmentWorkflowEvent _, CancellationToken _) =>
                    seen = snapshot
            )
            .Returns(Task.CompletedTask);

        await this.CreateDispatcher([handler.Object]).DispatchAsync(NewStatusEvent("Delivered"));

        Assert.NotNull(seen);
        Assert.True(seen.IsBookingSubmitted);
        Assert.Equal(ShipmentStatus.BookingConfirmed, seen.Status);
    }

    [Fact]
    public async Task DispatchAsync_WithAnUnknownOperationType_CompletesWithoutThrowing()
    {
        // The queue carries events for more than this service. Failing on one that was never ours
        // would dead-letter a perfectly good message.
        var handler = NewStateHandler(canHandle: true);

        var workflowEvent = new ShipmentWorkflowEvent
        {
            EventType = WorkflowEventType.ShipmentCommand,
            CorrelationId = "corr-1",
            Command = new CommandBase { OperationType = OperationType.Unknown },
            RawBody = """{"operationType":"SomethingElse"}""",
        };

        await this.CreateDispatcher([handler.Object]).DispatchAsync(workflowEvent);

        handler.Verify(
            h =>
                h.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task DispatchAsync_WhenNoHandlerClaimsTheEvent_CompletesWithoutThrowing()
    {
        this.GivenSnapshotExists(NewSnapshot());
        var declining = NewStateHandler(canHandle: false);

        await this.CreateDispatcher([declining.Object]).DispatchAsync(NewStatusEvent("Delivered"));
    }

    [Fact]
    public async Task DispatchAsync_WhenAStatusEventArrivesBeforeItsShipment_ParksIt()
    {
        // The submit may be seconds behind on another partition. Parking keeps the event; failing
        // would eventually dead-letter it, and dropping it would lose a real delivery.
        this.snapshots
            .Setup(repository =>
                repository.GetAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((ShipmentSnapshot?)null);

        var handler = NewStateHandler(canHandle: true);

        await this.CreateDispatcher([handler.Object]).DispatchAsync(NewStatusEvent("Delivered"));

        this.snapshots.Verify(
            repository =>
                repository.SavePendingAsync(
                    "VW-1042-7",
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        handler.Verify(
            h =>
                h.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task DispatchAsync_WhenASubmitArrivesForANewShipment_StartsAFreshSnapshot()
    {
        this.snapshots
            .Setup(repository =>
                repository.GetAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((ShipmentSnapshot?)null);

        var handler = NewStateHandler(canHandle: true);

        var workflowEvent = new ShipmentWorkflowEvent
        {
            EventType = WorkflowEventType.ShipmentCommand,
            CorrelationId = "corr-1",
            Command = new SubmitShipmentCommand
            {
                OperationType = OperationType.SubmitShipment,
                BookingReference = "VW-1042-7",
                ConsigneeId = "acme-expo",
                SourceSystem = "bookings",
            },
            RawBody = "{}",
        };

        await this.CreateDispatcher([handler.Object]).DispatchAsync(workflowEvent);

        handler.Verify(
            h =>
                h.HandleAsync(
                    It.Is<ShipmentSnapshot>(snapshot => snapshot.BookingReference == "VW-1042-7"),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        this.snapshots.Verify(
            repository =>
                repository.SavePendingAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task DispatchAsync_NormalizesARetriedReferenceBeforeLookingUpTheSnapshot()
    {
        this.GivenSnapshotExists(NewSnapshot());
        var handler = NewStateHandler(canHandle: true);

        var workflowEvent = new ShipmentWorkflowEvent
        {
            EventType = WorkflowEventType.ShipmentCommand,
            CorrelationId = "corr-1",
            Command = new UpdateShipmentStatusCommand
            {
                OperationType = OperationType.UpdateShipmentStatus,
                BookingReference = "VW-1042-7-r2",
                Status = "Delivered",
                SourceSystem = "bookings",
            },
            RawBody = "{}",
        };

        await this.CreateDispatcher([handler.Object]).DispatchAsync(workflowEvent);

        this.snapshots.Verify(
            repository =>
                repository.GetAsync("bookings", "VW-1042-7", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task DispatchAsync_RoutesACallbackToTheCallbackChain()
    {
        var callbackHandler = new Mock<IForwarderCallbackHandler>();
        callbackHandler.Setup(h => h.CanHandle(It.IsAny<ShipmentWorkflowEvent>())).Returns(true);
        callbackHandler
            .Setup(h =>
                h.HandleAsync(It.IsAny<ShipmentWorkflowEvent>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((string?)null);

        var stateHandler = NewStateHandler(canHandle: true);

        var dispatcher = new ShipmentWorkflowDispatcher(
            [stateHandler.Object],
            [callbackHandler.Object],
            this.snapshots.Object,
            new CommandEnvelopeFactory(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            NullLogger<ShipmentWorkflowDispatcher>.Instance
        );

        await dispatcher.DispatchAsync(
            new ShipmentWorkflowEvent
            {
                EventType = WorkflowEventType.BookingCallback,
                CorrelationId = "corr-1",
                Callback = new ForwarderCallback { ExternalId = "FWD-88421" },
                RawBody = "{}",
            }
        );

        callbackHandler.Verify(
            h => h.HandleAsync(It.IsAny<ShipmentWorkflowEvent>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        stateHandler.Verify(
            h =>
                h.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task DispatchAsync_WhenAHandlerThrows_LetsItPropagate()
    {
        // Deliberate: the bus's own retry and dead-letter policy is the recovery mechanism, so a
        // handler failure has to reach it rather than being swallowed here.
        this.GivenSnapshotExists(NewSnapshot());
        var handler = NewStateHandler(canHandle: true);
        handler
            .Setup(h =>
                h.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("handler failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => this.CreateDispatcher([handler.Object]).DispatchAsync(NewStatusEvent("Delivered"))
        );
    }

    private ShipmentWorkflowDispatcher CreateDispatcher(
        IShipmentStateHandler[] handlers,
        IForwarderCallbackHandler[]? callbackHandlers = null
    ) =>
        new(
            handlers,
            callbackHandlers ?? [],
            this.snapshots.Object,
            new CommandEnvelopeFactory(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            NullLogger<ShipmentWorkflowDispatcher>.Instance
        );

    private void GivenSnapshotExists(ShipmentSnapshot snapshot) =>
        this.snapshots
            .Setup(repository =>
                repository.GetAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(snapshot);

    private static Mock<IShipmentStateHandler> NewStateHandler(bool canHandle)
    {
        var handler = new Mock<IShipmentStateHandler>();
        handler
            .Setup(h => h.CanHandle(It.IsAny<ShipmentSnapshot>(), It.IsAny<ShipmentWorkflowEvent>()))
            .Returns(canHandle);
        handler
            .Setup(h =>
                h.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(Task.CompletedTask);
        return handler;
    }

    private static ShipmentWorkflowEvent NewStatusEvent(string status) =>
        new()
        {
            EventType = WorkflowEventType.ShipmentCommand,
            CorrelationId = "corr-1",
            Command = new UpdateShipmentStatusCommand
            {
                OperationType = OperationType.UpdateShipmentStatus,
                BookingReference = "VW-1042-7",
                Status = status,
                SourceSystem = "bookings",
            },
            RawBody = """{"operationType":"UpdateShipmentStatus"}""",
        };

    private static ShipmentSnapshot NewSnapshot() =>
        new()
        {
            BookingReference = "VW-1042-7",
            SourceSystem = "bookings",
            ConsigneeId = "acme-expo",
            Status = ShipmentStatus.BookingSubmitted,
        };
}
