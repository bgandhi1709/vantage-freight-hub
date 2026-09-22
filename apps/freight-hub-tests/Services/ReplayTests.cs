using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services;
using Vantage.Freight.Hub.Services.Callbacks;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Tests.Services;

/// <summary>
/// Parking an early event is only half a mechanism; something has to drain the queue. These cover
/// the other half, which is the part that is easy to leave unfinished and impossible to notice
/// missing — the shipment simply stops progressing and nothing fails.
/// </summary>
public sealed class ReplayTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly Mock<IShipmentSnapshotRepository> snapshots = new();
    private readonly Mock<IShipmentLifecycleTable> lifecycle = new();

    [Fact]
    public async Task BookingCallback_ReportsTheShipmentItUnblocked()
    {
        // The handler reports rather than replaying: replay means re-entering dispatch, and a
        // handler that called the dispatcher back is a cycle the container refuses to build.
        var snapshot = NewSnapshot();
        this.GivenSnapshotByForwarderReference(snapshot);

        var unblocked = await this.CreateBookingHandler().HandleAsync(NewBookingCallback());

        Assert.Equal("VW-1042-7", unblocked);
    }

    [Fact]
    public async Task BookingCallback_WhenTheForwarderRejectedTheBooking_UnblocksNothing()
    {
        this.GivenSnapshotByForwarderReference(NewSnapshot());

        var unblocked = await this.CreateBookingHandler()
            .HandleAsync(NewBookingCallback(isError: true, message: "no capacity on that lane"));

        Assert.Null(unblocked);
        this.lifecycle.Verify(
            table =>
                table.UpsertAsync(
                    It.Is<ShipmentLifecycleRecord>(record =>
                        record.Status == ShipmentStatus.BookingFailed
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task BookingCallback_WhenNoShipmentMatches_RecordsItAndUnblocksNothing()
    {
        // An unmatched callback is a fact worth seeing in the log, not a reason to fail a message
        // the forwarder would then redeliver forever.
        this.snapshots
            .Setup(repository =>
                repository.GetByForwarderReferenceAsync(
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((ShipmentSnapshot?)null);

        var unblocked = await this.CreateBookingHandler().HandleAsync(NewBookingCallback());

        Assert.Null(unblocked);
        this.lifecycle.Verify(
            table =>
                table.UpsertAsync(
                    It.IsAny<ShipmentLifecycleRecord>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task Dispatcher_ReplaysCommandsParkedOnTheSnapshot()
    {
        var snapshot = NewSnapshot();
        snapshot.PendingStatusCommands.Add(NewDeliveredCommand());
        this.GivenStoredSnapshot(snapshot);
        var stateHandler = NewStateHandler();

        await this.CreateDispatcher(stateHandler.Object).DispatchAsync(NewBookingCallback());

        stateHandler.Verify(
            handler =>
                handler.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.Is<ShipmentWorkflowEvent>(e =>
                        e.Command is UpdateShipmentStatusCommand
                        && ((UpdateShipmentStatusCommand)e.Command!).Status == "Delivered"
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Dispatcher_ReplaysRawBodiesItParkedEarlier()
    {
        // Events parked before their shipment existed live in blob storage rather than on the
        // snapshot, and both sources have to be drained.
        var snapshot = NewSnapshot();
        this.GivenStoredSnapshot(snapshot);
        this.snapshots
            .Setup(repository =>
                repository.GetPendingAsync("VW-1042-7", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                [
                    """
                    {"operationType":"UpdateShipmentStatus","bookingReference":"VW-1042-7","status":"Delivered"}
                    """,
                ]
            );
        var stateHandler = NewStateHandler();

        await this.CreateDispatcher(stateHandler.Object).DispatchAsync(NewBookingCallback());

        stateHandler.Verify(
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
    public async Task Dispatcher_ClearsTheQueueBeforeReplaying()
    {
        // If a replayed event throws, the bus retries the original message from the top. A queue
        // that still held the event would replay it a second time on that pass.
        var order = new List<string>();
        var snapshot = NewSnapshot();
        snapshot.PendingStatusCommands.Add(NewDeliveredCommand());
        this.GivenStoredSnapshot(snapshot);

        this.snapshots
            .Setup(repository =>
                repository.ClearPendingAsync("VW-1042-7", It.IsAny<CancellationToken>())
            )
            .Callback(() => order.Add("cleared"))
            .Returns(Task.CompletedTask);

        var stateHandler = NewStateHandler();
        stateHandler
            .Setup(handler =>
                handler.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => order.Add("replayed"))
            .Returns(Task.CompletedTask);

        await this.CreateDispatcher(stateHandler.Object).DispatchAsync(NewBookingCallback());

        Assert.Equal(["cleared", "replayed"], order);
        Assert.Empty(snapshot.PendingStatusCommands);
    }

    [Fact]
    public async Task Dispatcher_WithNothingParked_ReplaysNothing()
    {
        this.GivenStoredSnapshot(NewSnapshot());
        var stateHandler = NewStateHandler();

        await this.CreateDispatcher(stateHandler.Object).DispatchAsync(NewBookingCallback());

        stateHandler.Verify(
            handler =>
                handler.HandleAsync(
                    It.IsAny<ShipmentSnapshot>(),
                    It.IsAny<ShipmentWorkflowEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    private ShipmentWorkflowDispatcher CreateDispatcher(IShipmentStateHandler stateHandler) =>
        new(
            [stateHandler],
            [this.CreateBookingHandler()],
            this.snapshots.Object,
            new CommandEnvelopeFactory(Options),
            NullLogger<ShipmentWorkflowDispatcher>.Instance
        );

    private BookingCallbackHandler CreateBookingHandler() =>
        new(
            this.snapshots.Object,
            new ShipmentStateWriter(
                this.snapshots.Object,
                this.lifecycle.Object,
                TimeProvider.System,
                NullLogger<ShipmentStateWriter>.Instance
            ),
            NullLogger<BookingCallbackHandler>.Instance
        );

    private void GivenSnapshotByForwarderReference(ShipmentSnapshot snapshot) =>
        this.snapshots
            .Setup(repository =>
                repository.GetByForwarderReferenceAsync(
                    "FWD-88421",
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(snapshot);

    private void GivenStoredSnapshot(ShipmentSnapshot snapshot)
    {
        this.GivenSnapshotByForwarderReference(snapshot);
        this.snapshots
            .Setup(repository =>
                repository.GetAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(snapshot);
        this.snapshots
            .Setup(repository =>
                repository.GetPendingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);
    }

    private static Mock<IShipmentStateHandler> NewStateHandler()
    {
        var handler = new Mock<IShipmentStateHandler>();
        handler
            .Setup(h => h.CanHandle(It.IsAny<ShipmentSnapshot>(), It.IsAny<ShipmentWorkflowEvent>()))
            .Returns(true);
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

    private static ShipmentWorkflowEvent NewBookingCallback(
        bool isError = false,
        string message = ""
    ) =>
        new()
        {
            EventType = WorkflowEventType.BookingCallback,
            CorrelationId = "corr-1",
            Callback = new ForwarderCallback
            {
                EventType = "booking",
                ExternalId = "FWD-88421",
                IsError = isError,
                Message = message,
            },
            RawBody = "{}",
        };

    private static UpdateShipmentStatusCommand NewDeliveredCommand() =>
        new()
        {
            OperationType = OperationType.UpdateShipmentStatus,
            BookingReference = "VW-1042-7",
            Status = "Delivered",
            SourceSystem = "bookings",
        };

    private static ShipmentSnapshot NewSnapshot() =>
        new()
        {
            BookingReference = "VW-1042-7",
            SourceSystem = "bookings",
            ConsigneeId = "acme-expo",
            ForwarderReference = "FWD-88421",
            Status = ShipmentStatus.BookingSubmitted,
            IsBookingSubmitted = true,
        };
}
