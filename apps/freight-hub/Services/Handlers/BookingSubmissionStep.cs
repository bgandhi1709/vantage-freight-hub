using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Repository;

namespace Vantage.Freight.Hub.Services.Handlers;

/// <summary>
/// Books the freight with the forwarder. Shared, because two paths reach it: a submit for a
/// consignee whose account already exists, and the account callback replaying what it unblocked.
/// </summary>
internal sealed class BookingSubmissionStep(
    IForwarderClient forwarder,
    IShipmentSnapshotRepository snapshots,
    ShipmentStateWriter writer
)
{
    internal async Task SubmitAsync(
        ShipmentSnapshot snapshot,
        string correlationId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // The idempotency guard. Both callers can legitimately arrive for the same shipment, and
        // the forwarder would happily accept the same booking twice.
        if (snapshot.IsBookingSubmitted || snapshot.SubmitCommand is null)
        {
            return;
        }

        try
        {
            var ack = await forwarder
                .SubmitBookingAsync(snapshot.SubmitCommand, correlationId, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(ack.ForwarderReference))
            {
                snapshot.ForwarderReference = ack.ForwarderReference;
                await snapshots
                    .SaveForwarderIndexAsync(
                        ack.ForwarderReference,
                        snapshot.SourceSystem,
                        snapshot.BookingReference,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            snapshot.IsBookingSubmitted = true;
            await writer
                .WriteAsync(snapshot, ShipmentStatus.BookingSubmitted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ForwarderException ex)
        {
            await writer
                .WriteAsync(snapshot, ShipmentStatus.BookingFailed, cancellationToken, ex.Message)
                .ConfigureAwait(false);
            throw;
        }
    }
}
