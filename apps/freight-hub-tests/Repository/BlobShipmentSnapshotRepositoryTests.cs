using System.Text.Json;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;

namespace Vantage.Freight.Hub.Tests.Repository;

[Collection(AzuriteCollection.Name)]
public sealed class BlobShipmentSnapshotRepositoryTests(AzuriteFixture fixture)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private BlobShipmentSnapshotRepository CreateRepository() =>
        new(fixture.CreateContainer($"snap{Guid.NewGuid():N}"), Options);

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsTheSnapshot()
    {
        var repository = this.CreateRepository();
        var snapshot = NewSnapshot("VW-1042-7");
        snapshot.Status = ShipmentStatus.BookingConfirmed;
        snapshot.IsBookingSubmitted = true;

        await repository.SaveAsync(snapshot);
        var stored = await repository.GetAsync("bookings", "VW-1042-7");

        Assert.NotNull(stored);
        Assert.Equal(ShipmentStatus.BookingConfirmed, stored.Status);
        Assert.True(stored.IsBookingSubmitted);
        Assert.Equal(snapshot.ConsigneeId, stored.ConsigneeId);
    }

    [Fact]
    public async Task GetAsync_FindsASnapshotSavedUnderARetriedReference()
    {
        // A retry arrives as "VW-1042-7-r2" and must land on the same blob as the original, or the
        // forwarder ends up with two consignments for one booking.
        var repository = this.CreateRepository();

        await repository.SaveAsync(NewSnapshot("VW-1042-7-r2"));
        var stored = await repository.GetAsync("bookings", "VW-1042-7");

        Assert.NotNull(stored);
    }

    [Fact]
    public async Task GetAsync_IsUnaffectedByTheCasingOfTheReference()
    {
        // Blob names are case-sensitive. Every key goes through the normalizer for this reason.
        var repository = this.CreateRepository();

        await repository.SaveAsync(NewSnapshot("vw-1042-7"));
        var stored = await repository.GetAsync("BOOKINGS", "VW-1042-7");

        Assert.NotNull(stored);
    }

    [Fact]
    public async Task GetAsync_WhenNothingWasSaved_ReturnsNull()
    {
        var repository = this.CreateRepository();

        Assert.Null(await repository.GetAsync("bookings", "VW-9999-1"));
    }

    [Fact]
    public async Task GetByForwarderReferenceAsync_ResolvesThroughTheIndex()
    {
        // This is the whole point of the index: a callback carries only the forwarder's reference.
        var repository = this.CreateRepository();
        var snapshot = NewSnapshot("VW-1042-7");
        snapshot.ForwarderReference = "FWD-88421";

        await repository.SaveAsync(snapshot);
        await repository.SaveForwarderIndexAsync("FWD-88421", "bookings", "VW-1042-7");

        var stored = await repository.GetByForwarderReferenceAsync("FWD-88421");

        Assert.NotNull(stored);
        Assert.Equal("VW-1042-7", stored.BookingReference);
    }

    [Fact]
    public async Task GetByForwarderReferenceAsync_WithNoIndexEntry_ReturnsNull()
    {
        var repository = this.CreateRepository();
        await repository.SaveAsync(NewSnapshot("VW-1042-7"));

        Assert.Null(await repository.GetByForwarderReferenceAsync("FWD-UNKNOWN"));
    }

    [Fact]
    public async Task GetByForwarderReferenceAsync_IgnoresTheCasingOfTheForwarderReference()
    {
        var repository = this.CreateRepository();
        await repository.SaveAsync(NewSnapshot("VW-1042-7"));
        await repository.SaveForwarderIndexAsync("fwd-88421", "bookings", "VW-1042-7");

        Assert.NotNull(await repository.GetByForwarderReferenceAsync("FWD-88421"));
    }

    [Fact]
    public async Task SavePendingAsync_KeepsEveryParkedEventInArrivalOrder()
    {
        var repository = this.CreateRepository();

        await repository.SavePendingAsync("VW-1042-7", """{"seq":1}""");
        await repository.SavePendingAsync("VW-1042-7", """{"seq":2}""");
        await repository.SavePendingAsync("VW-1042-7", """{"seq":3}""");

        var pending = await repository.GetPendingAsync("VW-1042-7");

        Assert.Equal(3, pending.Count);
        Assert.Equal("""{"seq":1}""", pending[0]);
        Assert.Equal("""{"seq":3}""", pending[2]);
    }

    [Fact]
    public async Task SavePendingAsync_DoesNotCreateSomethingGetAsyncWouldMistakeForASnapshot()
    {
        // A parked event lives under its own prefix. If it landed on the snapshot path, the next
        // read would treat an unhandled event as an existing shipment.
        var repository = this.CreateRepository();

        await repository.SavePendingAsync("VW-1042-7", """{"seq":1}""");

        Assert.Null(await repository.GetAsync("bookings", "VW-1042-7"));
    }

    [Fact]
    public async Task ClearPendingAsync_EmptiesOnlyThatShipmentsQueue()
    {
        var repository = this.CreateRepository();
        await repository.SavePendingAsync("VW-1042-7", """{"seq":1}""");
        await repository.SavePendingAsync("VW-2000-1", """{"seq":1}""");

        await repository.ClearPendingAsync("VW-1042-7");

        Assert.Empty(await repository.GetPendingAsync("VW-1042-7"));
        Assert.Single(await repository.GetPendingAsync("VW-2000-1"));
    }

    [Fact]
    public async Task GetPendingAsync_WithNothingParked_ReturnsEmpty()
    {
        var repository = this.CreateRepository();

        Assert.Empty(await repository.GetPendingAsync("VW-1042-7"));
    }

    [Fact]
    public async Task SaveAsync_PreservesParkedCommandsCarriedOnTheSnapshot()
    {
        var repository = this.CreateRepository();
        var snapshot = NewSnapshot("VW-1042-7");
        snapshot.PendingStatusCommands.Add(
            new UpdateShipmentStatusCommand
            {
                BookingReference = "VW-1042-7",
                Status = "Delivered",
                OccurredUtc = DateTimeOffset.UtcNow,
            }
        );

        await repository.SaveAsync(snapshot);
        var stored = await repository.GetAsync("bookings", "VW-1042-7");

        Assert.NotNull(stored);
        Assert.Single(stored.PendingStatusCommands);
        Assert.Equal("Delivered", stored.PendingStatusCommands[0].Status);
    }

    private static ShipmentSnapshot NewSnapshot(string bookingReference) =>
        new()
        {
            BookingReference = bookingReference,
            SourceSystem = "bookings",
            ConsigneeId = "acme-expo",
            Status = ShipmentStatus.AccountCreationPending,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
        };
}
