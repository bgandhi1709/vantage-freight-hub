using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Models;

namespace Vantage.Freight.Hub.Tests.Infrastructure;

public sealed class ForwarderClientTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal HttpRequestMessage? LastRequest { get; private set; }

        internal string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            this.LastRequest = request;
            this.LastBody =
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ForwarderClient Client, CapturingHandler Handler) CreateClient(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"forwarderReference":"FWD-88421"}"""
    )
    {
        var handler = new CapturingHandler(status, body);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://forwarder.test/v1/"),
        };

        return (
            new ForwarderClient(httpClient, Options, NullLogger<ForwarderClient>.Instance),
            handler
        );
    }

    [Fact]
    public async Task SubmitBookingAsync_ReturnsTheForwardersOwnReference()
    {
        // That reference is the only identifier the later callbacks carry, so losing it here
        // means every subsequent callback for this shipment is unmatchable.
        var (client, _) = CreateClient();

        var ack = await client.SubmitBookingAsync(NewCommand(), "corr-1");

        Assert.Equal("FWD-88421", ack.ForwarderReference);
    }

    [Fact]
    public async Task SubmitBookingAsync_SendsTheCorrelationIdAsAHeader()
    {
        var (client, handler) = CreateClient();

        await client.SubmitBookingAsync(NewCommand(), "corr-42");

        Assert.True(handler.LastRequest!.Headers.TryGetValues("x-correlation-id", out var values));
        Assert.Equal("corr-42", Assert.Single(values!));
    }

    [Fact]
    public async Task CreateConsigneeAccountAsync_PostsToTheAccountsEndpoint()
    {
        var (client, handler) = CreateClient();

        await client.CreateConsigneeAccountAsync(NewCommand(), "corr-1");

        Assert.Equal("https://forwarder.test/v1/accounts", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("acme-expo", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitDeliveryAsync_AddressesTheForwardersOwnReference()
    {
        var (client, handler) = CreateClient();

        await client.SubmitDeliveryAsync("VW-1042-7", "FWD-88421", "corr-1");

        Assert.Equal(
            "https://forwarder.test/v1/bookings/FWD-88421/delivery",
            handler.LastRequest!.RequestUri!.ToString()
        );
    }

    [Fact]
    public async Task PostAsync_WhenTheForwarderRejectsTheCall_ThrowsWithTheStatusAndReference()
    {
        var (client, _) = CreateClient(HttpStatusCode.BadGateway, "{}");

        var error = await Assert.ThrowsAsync<ForwarderException>(
            () => client.SubmitBookingAsync(NewCommand(), "corr-1")
        );

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("VW-1042-7", error.BookingReference);
    }

    [Fact]
    public void RenderCurl_NeverContainsTheAuthorizationToken()
    {
        // This string is written to log storage. A token in a log is a leaked token, and the only
        // reliable way to keep it out is to never render it in the first place.
        var curl = ForwarderClient.RenderCurl(
            new Uri("https://forwarder.test/v1/"),
            "bookings",
            """{"bookingReference":"VW-1042-7"}"""
        );

        Assert.Contains("[REDACTED]", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer eyJ", curl, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderCurl_ProducesACommandThatCanBeReplayedByHand()
    {
        var curl = ForwarderClient.RenderCurl(
            new Uri("https://forwarder.test/v1/"),
            "bookings",
            """{"bookingReference":"VW-1042-7"}"""
        );

        Assert.StartsWith("curl -X POST 'https://forwarder.test/v1/bookings'", curl, StringComparison.Ordinal);
        Assert.Contains("""-d '{"bookingReference":"VW-1042-7"}'""", curl, StringComparison.Ordinal);
    }

    private static SubmitShipmentCommand NewCommand() =>
        new()
        {
            OperationType = OperationType.SubmitShipment,
            BookingReference = "VW-1042-7",
            ConsigneeId = "acme-expo",
            ConsigneeName = "Acme Expo",
            Venue = "Hall 3",
            CollectionDate = DateTimeOffset.UtcNow.AddDays(3),
            Pieces = 4,
            WeightKg = 118.5m,
            SourceSystem = "bookings",
        };
}
