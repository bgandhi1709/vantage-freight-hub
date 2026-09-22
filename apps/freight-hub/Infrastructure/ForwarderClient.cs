using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vantage.Freight.Hub.Logging;
using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Infrastructure;

public sealed class ForwarderException(string message, int statusCode, string bookingReference)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string BookingReference { get; } = bookingReference;
}

public sealed class ForwarderAcknowledgement
{
    /// <summary>The forwarder's own reference for this consignment. Its callbacks carry only this.</summary>
    public string ForwarderReference { get; set; } = string.Empty;
}

/// <summary>
/// The freight forwarder's API. Every call is accepted synchronously and completed later by a
/// callback, so nothing here returns a finished state — only an acknowledgement.
/// </summary>
public interface IForwarderClient
{
    Task<ForwarderAcknowledgement> CreateConsigneeAccountAsync(
        SubmitShipmentCommand command,
        string correlationId,
        CancellationToken cancellationToken = default
    );

    Task<ForwarderAcknowledgement> SubmitBookingAsync(
        SubmitShipmentCommand command,
        string correlationId,
        CancellationToken cancellationToken = default
    );

    Task SubmitDeliveryAsync(
        string bookingReference,
        string forwarderReference,
        string correlationId,
        CancellationToken cancellationToken = default
    );

    Task CancelBookingAsync(
        string bookingReference,
        string forwarderReference,
        string correlationId,
        CancellationToken cancellationToken = default
    );
}

internal sealed class ForwarderClient(
    HttpClient httpClient,
    JsonSerializerOptions serializerOptions,
    ILogger<ForwarderClient> logger
) : IForwarderClient
{
    /// <summary>Header the forwarder echoes back on the matching callback.</summary>
    private const string CorrelationHeader = "x-correlation-id";

    public Task<ForwarderAcknowledgement> CreateConsigneeAccountAsync(
        SubmitShipmentCommand command,
        string correlationId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        return this.PostAsync<ForwarderAcknowledgement>(
            "accounts",
            new
            {
                consigneeId = command.ConsigneeId,
                consigneeName = command.ConsigneeName,
            },
            command.BookingReference,
            correlationId,
            cancellationToken
        );
    }

    public Task<ForwarderAcknowledgement> SubmitBookingAsync(
        SubmitShipmentCommand command,
        string correlationId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        return this.PostAsync<ForwarderAcknowledgement>(
            "bookings",
            new
            {
                bookingReference = command.BookingReference,
                consigneeId = command.ConsigneeId,
                venue = command.Venue,
                collectionDate = command.CollectionDate,
                pieces = command.Pieces,
                weightKg = command.WeightKg,
            },
            command.BookingReference,
            correlationId,
            cancellationToken
        );
    }

    public async Task SubmitDeliveryAsync(
        string bookingReference,
        string forwarderReference,
        string correlationId,
        CancellationToken cancellationToken = default
    ) =>
        await this.PostAsync<ForwarderAcknowledgement>(
                string.Create(CultureInfo.InvariantCulture, $"bookings/{forwarderReference}/delivery"),
                new { bookingReference },
                bookingReference,
                correlationId,
                cancellationToken
            )
            .ConfigureAwait(false);

    public async Task CancelBookingAsync(
        string bookingReference,
        string forwarderReference,
        string correlationId,
        CancellationToken cancellationToken = default
    ) =>
        await this.PostAsync<ForwarderAcknowledgement>(
                string.Create(CultureInfo.InvariantCulture, $"bookings/{forwarderReference}/cancel"),
                new { bookingReference },
                bookingReference,
                correlationId,
                cancellationToken
            )
            .ConfigureAwait(false);

    private async Task<T> PostAsync<T>(
        string path,
        object payload,
        string bookingReference,
        string correlationId,
        CancellationToken cancellationToken
    )
        where T : new()
    {
        var body = JsonSerializer.Serialize(payload, serializerOptions);

        // The forwarder is a black box: when it rejects something, the only way to find out why is
        // to reproduce the call by hand. Logging the equivalent curl makes that a copy-paste rather
        // than an archaeology exercise. The token is replaced, never logged. Rendering it costs a
        // string build, so it only happens when the level is actually enabled.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var curl = RenderCurl(httpClient.BaseAddress, path, body);
            Log.OutboundCall(logger, correlationId, curl);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(CorrelationHeader, correlationId);

        using var response = await httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ForwarderException(
                $"The forwarder rejected POST {path}.",
                (int)response.StatusCode,
                bookingReference
            );
        }

        return await response
                .Content.ReadFromJsonAsync<T>(serializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? new T();
    }

    /// <summary>
    /// Renders the equivalent curl command. The authorization value is always the literal
    /// "[REDACTED]" — this string ends up in log storage, and a token there is a token leaked.
    /// </summary>
    internal static string RenderCurl(Uri? baseAddress, string path, string body)
    {
        var url = baseAddress is null ? path : new Uri(baseAddress, path).ToString();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"curl -X POST '{url}' -H 'Content-Type: application/json' "
                + $"-H 'Authorization: Bearer [REDACTED]' -d '{body}'"
        );
    }
}
