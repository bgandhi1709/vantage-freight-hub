using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;

namespace Vantage.Freight.Hub.Tests.Repository;

[Collection(AzuriteCollection.Name)]
public sealed class ShipmentLifecycleTableTests(AzuriteFixture fixture)
{
    private ShipmentLifecycleTable CreateTable() =>
        new(fixture.CreateTable($"lifecycle{Guid.NewGuid():N}"));

    [Fact]
    public async Task UpsertAsync_ThenGetAsync_RoundTripsTheRecord()
    {
        var table = this.CreateTable();
        var record = NewRecord("VW-1042-7", ShipmentStatus.BookingSubmitted);

        await table.UpsertAsync(record);
        var stored = await table.GetAsync("bookings", "acme-expo", "VW-1042-7");

        Assert.NotNull(stored);
        Assert.Equal(ShipmentStatus.BookingSubmitted, stored.Status);
        Assert.Equal("FWD-88421", stored.ForwarderReference);
    }

    [Fact]
    public async Task UpsertAsync_ReplacesTheRowRatherThanMergingIntoIt()
    {
        // The row is written whole from the snapshot. A merge could leave a stale failure message
        // attached to a shipment that has since succeeded.
        var table = this.CreateTable();
        var failed = NewRecord("VW-1042-7", ShipmentStatus.BookingFailed) with
        {
            LastFailureMessage = "forwarder rejected the booking",
        };
        await table.UpsertAsync(failed);

        await table.UpsertAsync(NewRecord("VW-1042-7", ShipmentStatus.BookingConfirmed));
        var stored = await table.GetAsync("bookings", "acme-expo", "VW-1042-7");

        Assert.NotNull(stored);
        Assert.Equal(ShipmentStatus.BookingConfirmed, stored.Status);
        Assert.Equal(string.Empty, stored.LastFailureMessage);
    }

    [Fact]
    public async Task GetAsync_IsUnaffectedByTheCasingOfTheKeys()
    {
        // Partition and row keys compare case-sensitively; the normalizer is what makes a
        // reference typed one way match a row written another.
        var table = this.CreateTable();
        await table.UpsertAsync(NewRecord("vw-1042-7", ShipmentStatus.BookingConfirmed));

        Assert.NotNull(await table.GetAsync("BOOKINGS", "ACME-EXPO", "VW-1042-7"));
    }

    [Fact]
    public async Task GetAsync_TreatsARetriedReferenceAsTheSameShipment()
    {
        var table = this.CreateTable();
        await table.UpsertAsync(NewRecord("VW-1042-7-r3", ShipmentStatus.BookingConfirmed));

        Assert.NotNull(await table.GetAsync("bookings", "acme-expo", "VW-1042-7"));
    }

    [Fact]
    public async Task UpsertAsync_NormalizesTheTimestampToUtc()
    {
        // Table Storage returns UTC regardless of what it was given. A local-time write would
        // otherwise read back as a different instant.
        var table = this.CreateTable();
        var local = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.FromHours(2));
        await table.UpsertAsync(
            NewRecord("VW-1042-7", ShipmentStatus.BookingConfirmed) with { LastUpdatedUtc = local }
        );

        var stored = await table.GetAsync("bookings", "acme-expo", "VW-1042-7");

        Assert.NotNull(stored);
        Assert.Equal(TimeSpan.Zero, stored.LastUpdatedUtc.Offset);
        Assert.Equal(local.UtcDateTime, stored.LastUpdatedUtc.UtcDateTime);
    }

    [Fact]
    public async Task GetByStatusAsync_ReturnsOnlyTheShipmentsWaitingInThatStatus()
    {
        // This is the replay fan-out: everything parked behind account creation for one consignee.
        var table = this.CreateTable();
        await table.UpsertAsync(NewRecord("VW-1", ShipmentStatus.AccountCreationPending));
        await table.UpsertAsync(NewRecord("VW-2", ShipmentStatus.AccountCreationPending));
        await table.UpsertAsync(NewRecord("VW-3", ShipmentStatus.BookingConfirmed));

        var pending = await table.GetByStatusAsync(
            "bookings",
            "acme-expo",
            ShipmentStatus.AccountCreationPending
        );

        Assert.Equal(2, pending.Count);
        Assert.All(pending, record => Assert.Equal(ShipmentStatus.AccountCreationPending, record.Status));
    }

    [Fact]
    public async Task GetByStatusAsync_IsScopedToOneConsignee()
    {
        var table = this.CreateTable();
        await table.UpsertAsync(NewRecord("VW-1", ShipmentStatus.AccountCreationPending));
        await table.UpsertAsync(
            NewRecord("VW-2", ShipmentStatus.AccountCreationPending) with
            {
                ConsigneeId = "other-consignee",
            }
        );

        var pending = await table.GetByStatusAsync(
            "bookings",
            "acme-expo",
            ShipmentStatus.AccountCreationPending
        );

        Assert.Single(pending);
    }

    [Fact]
    public async Task GetAsync_WhenNothingWasWritten_ReturnsNull()
    {
        var table = this.CreateTable();

        Assert.Null(await table.GetAsync("bookings", "acme-expo", "VW-9999-1"));
    }

    private static ShipmentLifecycleRecord NewRecord(string reference, ShipmentStatus status) =>
        new()
        {
            SourceSystem = "bookings",
            BookingReference = reference,
            ForwarderReference = "FWD-88421",
            ConsigneeId = "acme-expo",
            Status = status,
            LastUpdatedUtc = DateTimeOffset.UtcNow,
        };
}
