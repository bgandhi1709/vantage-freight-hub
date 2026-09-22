using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services;
using Vantage.Freight.Hub.Services.Callbacks;
using Vantage.Freight.Hub.Services.Handlers;

namespace Vantage.Freight.Hub.Tests.Services;

public sealed class AccountCallbackHandlerTests
{
    private readonly Mock<IShipmentSnapshotRepository> snapshots = new();
    private readonly Mock<IShipmentLifecycleTable> lifecycle = new();
    private readonly Mock<IForwarderClient> forwarder = new();

    public AccountCallbackHandlerTests() =>
        this.forwarder
            .Setup(client =>
                client.SubmitBookingAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ForwarderAcknowledgement { ForwarderReference = "FWD-88421" });

    [Fact]
    public async Task HandleAsync_BooksTheShipmentThatAskedForTheAccount()
    {
        // The bug this pins: the handler moves its own shipment out of AccountCreationPending and
        // then fans out over everything still pending — which no longer includes itself. Without
        // booking it explicitly, the shipment that triggered account creation is the one shipment
        // that never gets booked, and nothing fails to say so.
        var snapshot = NewSnapshot("VW-1042-7");
        this.GivenSnapshotByForwarderReference(snapshot);
        this.GivenPendingShipments();

        await this.CreateHandler().HandleAsync(NewAccountCallback());

        this.forwarder.Verify(
            client =>
                client.SubmitBookingAsync(
                    It.Is<SubmitShipmentCommand>(command =>
                        command.BookingReference == "VW-1042-7"
                    ),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task HandleAsync_AlsoBooksEveryOtherShipmentParkedBehindTheAccount()
    {
        var snapshot = NewSnapshot("VW-1042-7");
        var waiting = NewSnapshot("VW-2000-1");
        this.GivenSnapshotByForwarderReference(snapshot);
        this.GivenPendingShipments("VW-2000-1");
        this.snapshots
            .Setup(repository =>
                repository.GetAsync("bookings", "VW-2000-1", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(waiting);

        await this.CreateHandler().HandleAsync(NewAccountCallback());

        this.forwarder.Verify(
            client =>
                client.SubmitBookingAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task HandleAsync_DoesNotBookTheSameShipmentTwice()
    {
        // The fan-out query can still return this shipment depending on write timing, and the
        // forwarder does not deduplicate bookings.
        var snapshot = NewSnapshot("VW-1042-7");
        this.GivenSnapshotByForwarderReference(snapshot);
        this.GivenPendingShipments("VW-1042-7");
        this.snapshots
            .Setup(repository =>
                repository.GetAsync("bookings", "VW-1042-7", It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(snapshot);

        await this.CreateHandler().HandleAsync(NewAccountCallback());

        this.forwarder.Verify(
            client =>
                client.SubmitBookingAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task HandleAsync_WhenTheAccountFailed_BooksNothing()
    {
        this.GivenSnapshotByForwarderReference(NewSnapshot("VW-1042-7"));
        this.GivenPendingShipments();

        await this.CreateHandler()
            .HandleAsync(NewAccountCallback(isError: true, message: "duplicate consignee"));

        this.forwarder.Verify(
            client =>
                client.SubmitBookingAsync(
                    It.IsAny<SubmitShipmentCommand>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
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

    private AccountCallbackHandler CreateHandler()
    {
        var writer = new ShipmentStateWriter(
            this.snapshots.Object,
            this.lifecycle.Object,
            TimeProvider.System,
            NullLogger<ShipmentStateWriter>.Instance
        );

        return new AccountCallbackHandler(
            this.snapshots.Object,
            this.lifecycle.Object,
            writer,
            new BookingSubmissionStep(this.forwarder.Object, this.snapshots.Object, writer),
            NullLogger<AccountCallbackHandler>.Instance
        );
    }

    private void GivenSnapshotByForwarderReference(ShipmentSnapshot snapshot) =>
        this.snapshots
            .Setup(repository =>
                repository.GetByForwarderReferenceAsync(
                    "FWD-88421",
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(snapshot);

    private void GivenPendingShipments(params string[] references) =>
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
                    .. references.Select(reference => new ShipmentLifecycleRecord
                    {
                        SourceSystem = "bookings",
                        BookingReference = reference,
                        ConsigneeId = "acme-expo",
                        Status = ShipmentStatus.AccountCreationPending,
                    }),
                ]
            );

    private static ShipmentWorkflowEvent NewAccountCallback(
        bool isError = false,
        string message = ""
    ) =>
        new()
        {
            EventType = WorkflowEventType.AccountCallback,
            CorrelationId = "corr-1",
            Callback = new ForwarderCallback
            {
                EventType = "account",
                ExternalId = "FWD-88421",
                IsError = isError,
                Message = message,
            },
            RawBody = "{}",
        };

    private static ShipmentSnapshot NewSnapshot(string bookingReference) =>
        new()
        {
            BookingReference = bookingReference,
            SourceSystem = "bookings",
            ConsigneeId = "acme-expo",
            ForwarderReference = "FWD-88421",
            Status = ShipmentStatus.AccountCreationPending,
            SubmitCommand = new SubmitShipmentCommand
            {
                OperationType = OperationType.SubmitShipment,
                BookingReference = bookingReference,
                ConsigneeId = "acme-expo",
                SourceSystem = "bookings",
            },
        };
}
