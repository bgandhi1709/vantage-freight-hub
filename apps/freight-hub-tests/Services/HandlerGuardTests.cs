using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Tests.Services;

/// <summary>
/// The guards are where this service's real decisions live, so they are tested as behaviour rather
/// than through the handlers that follow them.
/// </summary>
public sealed class HandlerGuardTests
{
    private readonly Mock<IForwarderClient> forwarder = new();
    private readonly Mock<IShipmentSnapshotRepository> snapshots = new();
    private readonly Mock<IShipmentLifecycleTable> lifecycle = new();

    [Theory]
    [InlineData(ShipmentStatus.BookingConfirmed, false, true)]
    [InlineData(ShipmentStatus.PickupConfirmed, false, true)]
    [InlineData(ShipmentStatus.DeliveryFailed, false, true)]
    [InlineData(ShipmentStatus.BookingSubmitted, false, false)]
    [InlineData(ShipmentStatus.AccountCreationPending, false, false)]
    [InlineData(ShipmentStatus.BookingConfirmed, true, false)]
    public void DeliverySubmissionHandler_ClaimsADeliveryOnlyWhenTheBookingIsConfirmed(
        ShipmentStatus status,
        bool alreadySubmitted,
        bool expected
    )
    {
        var handler = new DeliverySubmissionHandler(this.forwarder.Object, this.CreateWriter());
        var snapshot = NewSnapshot(status);
        snapshot.IsDeliverySubmitted = alreadySubmitted;

        Assert.Equal(expected, handler.CanHandle(snapshot, NewStatusEvent("Delivered")));
    }

    [Theory]
    [InlineData(ShipmentStatus.BookingSubmitted, true)]
    [InlineData(ShipmentStatus.AccountCreationPending, true)]
    [InlineData(ShipmentStatus.BookingConfirmed, false)]
    [InlineData(ShipmentStatus.PickupConfirmed, false)]
    public void DeferredDeliveryHandler_ClaimsExactlyWhatTheSubmissionHandlerDeclines(
        ShipmentStatus status,
        bool expected
    )
    {
        // The two guards are inverses. If that ever stops being true, some delivery event is
        // either handled twice or silently dropped, and this test is what catches it.
        var deferred = new DeferredDeliveryHandler(
            this.snapshots.Object,
            NullLogger<DeferredDeliveryHandler>.Instance
        );
        var submission = new DeliverySubmissionHandler(this.forwarder.Object, this.CreateWriter());
        var snapshot = NewSnapshot(status);
        var workflowEvent = NewStatusEvent("Delivered");

        Assert.Equal(expected, deferred.CanHandle(snapshot, workflowEvent));
        Assert.NotEqual(
            deferred.CanHandle(snapshot, workflowEvent),
            submission.CanHandle(snapshot, workflowEvent)
        );
    }

    [Fact]
    public async Task DeferredDeliveryHandler_KeepsTheEventOnTheSnapshotInsteadOfDroppingIt()
    {
        var handler = new DeferredDeliveryHandler(
            this.snapshots.Object,
            NullLogger<DeferredDeliveryHandler>.Instance
        );
        var snapshot = NewSnapshot(ShipmentStatus.BookingSubmitted);

        await handler.HandleAsync(snapshot, NewStatusEvent("Delivered"));

        Assert.Single(snapshot.PendingStatusCommands);
        this.snapshots.Verify(
            repository => repository.SaveAsync(snapshot, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Theory]
    [InlineData(true, ShipmentStatus.BookingSubmitted, true)]
    [InlineData(false, ShipmentStatus.BookingSubmitted, false)]
    [InlineData(true, ShipmentStatus.Cancelled, false)]
    public void CancellationHandler_ClaimsACancellationOnlyForABookingTheForwarderKnowsAbout(
        bool bookingSubmitted,
        ShipmentStatus status,
        bool expected
    )
    {
        var handler = new CancellationHandler(this.forwarder.Object, this.CreateWriter());
        var snapshot = NewSnapshot(status);
        snapshot.IsBookingSubmitted = bookingSubmitted;

        Assert.Equal(expected, handler.CanHandle(snapshot, NewStatusEvent("Cancelled")));
    }

    [Fact]
    public void CancellationHandler_DoesNotClaimADeliveryEvent()
    {
        var handler = new CancellationHandler(this.forwarder.Object, this.CreateWriter());
        var snapshot = NewSnapshot(ShipmentStatus.BookingConfirmed);
        snapshot.IsBookingSubmitted = true;

        Assert.False(handler.CanHandle(snapshot, NewStatusEvent("Delivered")));
    }

    [Fact]
    public async Task SubmitShipmentHandler_WritesPendingToBothStoresBeforeCallingTheForwarder()
    {
        // Ordering guarantee: if the outbound call is in flight, stored state already says so. The
        // other way round, a crash mid-call leaves an account request nothing accounts for, and the
        // retry creates a second account for one consignee.
        var effects = new List<string>();

        this.snapshots
            .Setup(repository =>
                repository.SaveAsync(It.IsAny<ShipmentSnapshot>(), It.IsAny<CancellationToken>())
            )
            .Callback(() => effects.Add("snapshot"))
            .Returns(Task.CompletedTask);
        this.lifecycle
            .Setup(table =>
                table.UpsertAsync(
                    It.IsAny<ShipmentLifecycleRecord>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => effects.Add("table"))
            .Returns(Task.CompletedTask);
        this.lifecycle
            .Setup(table =>
                table.GetByStatusAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<ShipmentStatus>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        this.forwarder
            .Setup(client =>
                client.CreateConsigneeAccountAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(() => effects.Add("forwarder"))
            .ReturnsAsync(new ForwarderAcknowledgement { ForwarderReference = "FWD-1" });

        var handler = this.CreateSubmitHandler();

        await handler.HandleAsync(
            new ShipmentSnapshot { BookingReference = "VW-1042-7", SourceSystem = "bookings" },
            NewSubmitEvent()
        );

        Assert.Equal(["snapshot", "table", "forwarder"], effects);
    }

    [Fact]
    public async Task SubmitShipmentHandler_WhenTheForwarderFails_MarksFailedAndRethrows()
    {
        this.lifecycle
            .Setup(table =>
                table.GetByStatusAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<ShipmentStatus>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        this.forwarder
            .Setup(client =>
                client.CreateConsigneeAccountAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new ForwarderException("rejected", 500, "VW-1042-7"));

        var handler = this.CreateSubmitHandler();

        await Assert.ThrowsAsync<ForwarderException>(
            () =>
                handler.HandleAsync(
                    new ShipmentSnapshot
                    {
                        BookingReference = "VW-1042-7",
                        SourceSystem = "bookings",
                    },
                    NewSubmitEvent()
                )
        );

        this.lifecycle.Verify(
            table =>
                table.UpsertAsync(
                    It.Is<ShipmentLifecycleRecord>(record =>
                        record.Status == ShipmentStatus.AccountCreationFailed
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task SubmitShipmentHandler_WhenAnotherShipmentAlreadyAskedForTheAccount_DoesNotAskAgain()
    {
        // The forwarder does not deduplicate account creation, so a second request for the same
        // consignee produces a second account.
        this.lifecycle
            .Setup(table =>
                table.GetByStatusAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    ShipmentStatus.AccountCreationPending,
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                [
                    new ShipmentLifecycleRecord
                    {
                        SourceSystem = "bookings",
                        BookingReference = "VW-9-1",
                        ConsigneeId = "acme-expo",
                        Status = ShipmentStatus.AccountCreationPending,
                    },
                ]
            );

        var handler = this.CreateSubmitHandler();

        await handler.HandleAsync(
            new ShipmentSnapshot { BookingReference = "VW-1042-7", SourceSystem = "bookings" },
            NewSubmitEvent()
        );

        this.forwarder.Verify(
            client =>
                client.CreateConsigneeAccountAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    private SubmitShipmentHandler CreateSubmitHandler()
    {
        var writer = this.CreateWriter();

        return new SubmitShipmentHandler(
            this.forwarder.Object,
            this.snapshots.Object,
            this.lifecycle.Object,
            writer,
            new BookingSubmissionStep(this.forwarder.Object, this.snapshots.Object, writer),
            NullLogger<SubmitShipmentHandler>.Instance
        );
    }

    private ShipmentStateWriter CreateWriter() =>
        new(
            this.snapshots.Object,
            this.lifecycle.Object,
            TimeProvider.System,
            NullLogger<ShipmentStateWriter>.Instance
        );

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
            },
            RawBody = "{}",
        };

    private static ShipmentWorkflowEvent NewSubmitEvent() =>
        new()
        {
            EventType = WorkflowEventType.ShipmentCommand,
            CorrelationId = "corr-1",
            Command = new SubmitShipmentCommand
            {
                OperationType = OperationType.SubmitShipment,
                BookingReference = "VW-1042-7",
                ConsigneeId = "acme-expo",
                ConsigneeName = "Acme Expo",
                SourceSystem = "bookings",
            },
            RawBody = "{}",
        };

    private static ShipmentSnapshot NewSnapshot(ShipmentStatus status) =>
        new()
        {
            BookingReference = "VW-1042-7",
            SourceSystem = "bookings",
            ConsigneeId = "acme-expo",
            Status = status,
        };
}
