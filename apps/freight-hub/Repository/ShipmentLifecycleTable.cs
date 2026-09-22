using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Repository;

/// <summary>Queryable lifecycle row. Derived from the snapshot and rebuildable from it.</summary>
internal sealed class ShipmentLifecycleEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;

    public string RowKey { get; set; } = string.Empty;

    public DateTimeOffset? Timestamp { get; set; }

    public ETag ETag { get; set; }

    public string BookingReference { get; set; } = string.Empty;

    public string ForwarderReference { get; set; } = string.Empty;

    public string ConsigneeId { get; set; } = string.Empty;

    /// <summary>Stored by name, matching the snapshot. One representation, both stores.</summary>
    public string Status { get; set; } = string.Empty;

    public string LastFailureMessage { get; set; } = string.Empty;

    public DateTimeOffset LastUpdatedUtc { get; set; }
}

/// <summary>Immutable lifecycle projection. A record so tests and callers can vary one field with `with`.</summary>
public sealed record ShipmentLifecycleRecord
{
    public required string SourceSystem { get; init; }

    public required string BookingReference { get; init; }

    public string ForwarderReference { get; init; } = string.Empty;

    public string ConsigneeId { get; init; } = string.Empty;

    public ShipmentStatus Status { get; init; }

    public string LastFailureMessage { get; init; } = string.Empty;

    public DateTimeOffset LastUpdatedUtc { get; init; }
}

/// <summary>
/// The queryable index over the snapshots: "what is in flight for this consignee", "which
/// shipments are parked waiting on an account".
/// </summary>
public interface IShipmentLifecycleTable
{
    Task UpsertAsync(
        ShipmentLifecycleRecord record,
        CancellationToken cancellationToken = default
    );

    Task<ShipmentLifecycleRecord?> GetAsync(
        string sourceSystem,
        string consigneeId,
        string bookingReference,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Every shipment for a consignee sitting in the given status. This is the fan-out used to
    /// replay bookings that were waiting on the forwarder to create the account.
    /// </summary>
    Task<IReadOnlyList<ShipmentLifecycleRecord>> GetByStatusAsync(
        string sourceSystem,
        string consigneeId,
        ShipmentStatus status,
        CancellationToken cancellationToken = default
    );
}

internal sealed class ShipmentLifecycleTable(TableClient table) : IShipmentLifecycleTable
{
    public async Task UpsertAsync(
        ShipmentLifecycleRecord record,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(record);

        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

        var entity = new ShipmentLifecycleEntity
        {
            PartitionKey = Partition(record.SourceSystem, record.ConsigneeId),
            RowKey = ReferenceNormalizer.ToStorageKey(record.BookingReference),
            BookingReference = record.BookingReference,
            ForwarderReference = record.ForwarderReference,
            ConsigneeId = record.ConsigneeId,
            Status = record.Status.ToString(),
            LastFailureMessage = record.LastFailureMessage,
            LastUpdatedUtc = record.LastUpdatedUtc.ToUniversalTime(),
        };

        // Replace, not merge: the snapshot is the source of truth and this row is written whole
        // from it, so a partial merge could leave a field from an older write in place.
        await table
            .UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ShipmentLifecycleRecord?> GetAsync(
        string sourceSystem,
        string consigneeId,
        string bookingReference,
        CancellationToken cancellationToken = default
    )
    {
        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

        var response = await table
            .GetEntityIfExistsAsync<ShipmentLifecycleEntity>(
                Partition(sourceSystem, consigneeId),
                ReferenceNormalizer.ToStorageKey(bookingReference),
                cancellationToken: cancellationToken
            )
            .ConfigureAwait(false);

        return response.HasValue ? ToRecord(sourceSystem, response.Value!) : null;
    }

    public async Task<IReadOnlyList<ShipmentLifecycleRecord>> GetByStatusAsync(
        string sourceSystem,
        string consigneeId,
        ShipmentStatus status,
        CancellationToken cancellationToken = default
    )
    {
        await table.CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);

        var partition = Partition(sourceSystem, consigneeId);
        var name = status.ToString();
        var records = new List<ShipmentLifecycleRecord>();

        // Single-partition query with the status filtered server-side.
        var query = table.QueryAsync<ShipmentLifecycleEntity>(
            entity => entity.PartitionKey == partition && entity.Status == name,
            cancellationToken: cancellationToken
        );

        await foreach (var entity in query.ConfigureAwait(false))
        {
            records.Add(ToRecord(sourceSystem, entity));
        }

        return records;
    }

    /// <summary>
    /// Partitioned by source system and consignee, because every query this service makes is
    /// "what is happening for this consignee" — never "every shipment everywhere".
    /// </summary>
    private static string Partition(string sourceSystem, string consigneeId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{sourceSystem.ToLowerInvariant()}|{consigneeId.ToLowerInvariant()}"
        );

    private static ShipmentLifecycleRecord ToRecord(
        string sourceSystem,
        ShipmentLifecycleEntity entity
    ) =>
        new()
        {
            SourceSystem = sourceSystem,
            BookingReference = entity.BookingReference,
            ForwarderReference = entity.ForwarderReference,
            ConsigneeId = entity.ConsigneeId,
            Status = Enum.TryParse<ShipmentStatus>(entity.Status, out var parsed)
                ? parsed
                : ShipmentStatus.Unknown,
            LastFailureMessage = entity.LastFailureMessage,
            LastUpdatedUtc = entity.LastUpdatedUtc,
        };
}
