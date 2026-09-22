using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Repository;

/// <summary>
/// Blob-backed canonical store.
/// </summary>
/// <remarks>
/// Layout, all under one container:
/// <code>
///   shipments/{source}/{reference}.json     the snapshot
///   index/{forwarderReference}.json         forwarder reference → {source, reference}
///   pending/{reference}/{timestamp}.json    events that arrived before their shipment
/// </code>
/// The index exists because the forwarder's callbacks carry only its own reference. Without it, a
/// callback could not be matched to anything and the lifecycle would stall at whatever step last
/// went out. The pending prefix is deliberately separate from the snapshot path: a parked event
/// must never be mistaken for a shipment that exists.
/// </remarks>
internal sealed class BlobShipmentSnapshotRepository(
    BlobContainerClient container,
    JsonSerializerOptions serializerOptions
) : IShipmentSnapshotRepository
{
    private sealed class ForwarderIndexEntry
    {
        public string SourceSystem { get; set; } = string.Empty;

        public string BookingReference { get; set; } = string.Empty;
    }

    public async Task SaveAsync(
        ShipmentSnapshot snapshot,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await this.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var path = SnapshotPath(snapshot.SourceSystem, snapshot.BookingReference);
        await this.UploadAsync(path, JsonSerializer.Serialize(snapshot, serializerOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ShipmentSnapshot?> GetAsync(
        string sourceSystem,
        string bookingReference,
        CancellationToken cancellationToken = default
    )
    {
        var body = await this
            .DownloadAsync(SnapshotPath(sourceSystem, bookingReference), cancellationToken)
            .ConfigureAwait(false);

        return body is null
            ? null
            : JsonSerializer.Deserialize<ShipmentSnapshot>(body, serializerOptions);
    }

    public async Task<ShipmentSnapshot?> GetByForwarderReferenceAsync(
        string forwarderReference,
        CancellationToken cancellationToken = default
    )
    {
        var indexBody = await this
            .DownloadAsync(IndexPath(forwarderReference), cancellationToken)
            .ConfigureAwait(false);

        if (indexBody is null)
        {
            return null;
        }

        var entry = JsonSerializer.Deserialize<ForwarderIndexEntry>(indexBody, serializerOptions);

        return entry is null
            ? null
            : await this.GetAsync(entry.SourceSystem, entry.BookingReference, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task SaveForwarderIndexAsync(
        string forwarderReference,
        string sourceSystem,
        string bookingReference,
        CancellationToken cancellationToken = default
    )
    {
        await this.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var entry = new ForwarderIndexEntry
        {
            SourceSystem = sourceSystem,
            BookingReference = ReferenceNormalizer.ToStorageKey(bookingReference),
        };

        await this.UploadAsync(
                IndexPath(forwarderReference),
                JsonSerializer.Serialize(entry, serializerOptions),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task SavePendingAsync(
        string bookingReference,
        string rawBody,
        CancellationToken cancellationToken = default
    )
    {
        await this.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        // Timestamped, so several events parked for the same shipment all survive and replay in
        // the order they arrived.
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"pending/{ReferenceNormalizer.ToStorageKey(bookingReference)}/{DateTime.UtcNow.Ticks:D19}.json"
        );

        await this.UploadAsync(path, rawBody, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetPendingAsync(
        string bookingReference,
        CancellationToken cancellationToken = default
    )
    {
        await this.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"pending/{ReferenceNormalizer.ToStorageKey(bookingReference)}/"
        );

        var names = new List<string>();

        await foreach (
            var blob in container
                .GetBlobsAsync(
                    BlobTraits.None,
                    BlobStates.None,
                    prefix,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            names.Add(blob.Name);
        }

        names.Sort(StringComparer.Ordinal);

        var bodies = new List<string>(names.Count);

        foreach (var name in names)
        {
            var body = await this.DownloadAsync(name, cancellationToken).ConfigureAwait(false);

            if (body is not null)
            {
                bodies.Add(body);
            }
        }

        return bodies;
    }

    public async Task ClearPendingAsync(
        string bookingReference,
        CancellationToken cancellationToken = default
    )
    {
        await this.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        var prefix = string.Create(
            CultureInfo.InvariantCulture,
            $"pending/{ReferenceNormalizer.ToStorageKey(bookingReference)}/"
        );

        await foreach (
            var blob in container
                .GetBlobsAsync(
                    BlobTraits.None,
                    BlobStates.None,
                    prefix,
                    cancellationToken
                )
                .ConfigureAwait(false)
        )
        {
            await container
                .DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureContainerAsync(CancellationToken cancellationToken) =>
        await container
            .CreateIfNotExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private async Task UploadAsync(string path, string body, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await container
            .GetBlobClient(path)
            .UploadAsync(content, overwrite: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(path);

        if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return response.Value.Content.ToString();
    }

    // Forward slashes only: a blob name is one flat string, and a backslash produces a path that
    // looks nested in code and is not nested in any tool that reads the container.
    private static string SnapshotPath(string sourceSystem, string bookingReference) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"shipments/{sourceSystem.ToLowerInvariant()}/{ReferenceNormalizer.ToStorageKey(bookingReference)}.json"
        );

    private static string IndexPath(string forwarderReference) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"index/{forwarderReference.Trim().ToLowerInvariant()}.json"
        );
}
