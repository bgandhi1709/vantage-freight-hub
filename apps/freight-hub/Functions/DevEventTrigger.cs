using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vantage.Freight.Hub.Configuration;
using Vantage.Freight.Hub.Models;
using Vantage.Freight.Hub.Services;

namespace Vantage.Freight.Hub.Functions;

/// <summary>
/// Feeds the same dispatcher over HTTP, so the whole lifecycle can be driven with curl.
/// </summary>
/// <remarks>
/// The bus triggers are the production entry point; this exists so the service is demonstrable
/// without a Service Bus namespace, and so an out-of-order sequence can be reproduced deliberately
/// rather than waited for. It goes through the identical dispatcher — a separate code path for
/// local runs would only prove that the local path works.
///
/// It is authenticated: the function is anonymous to the platform, so the API key is this
/// endpoint's only gate, and it is compared in fixed time.
/// </remarks>
internal sealed class DevEventTrigger(
    IShipmentWorkflowDispatcher dispatcher,
    ICommandEnvelopeFactory envelopeFactory,
    JsonSerializerOptions serializerOptions,
    IOptions<FreightHubOptions> options,
    ILogger<DevEventTrigger> logger
)
{
    [Function("DevEvents")]
    public async Task<IActionResult> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "dev/events")]
            HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsAuthorized(request, options.Value.DevEndpointApiKey))
        {
            return new UnauthorizedResult();
        }

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        // The event type travels as a query parameter so one endpoint can raise a command or any
        // of the four callbacks.
        var eventType = Enum.TryParse<WorkflowEventType>(
            request.Query["eventType"].ToString(),
            ignoreCase: true,
            out var parsed
        )
            ? parsed
            : WorkflowEventType.ShipmentCommand;

        var correlationId = Guid.NewGuid().ToString("N");

        using var scope = logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = correlationId }
        );

        var workflowEvent =
            eventType == WorkflowEventType.ShipmentCommand
                ? BuildCommandEvent(body, correlationId)
                : new ShipmentWorkflowEvent
                {
                    EventType = eventType,
                    CorrelationId = correlationId,
                    Callback = JsonSerializer.Deserialize<ForwarderCallback>(
                        body,
                        serializerOptions
                    ),
                    RawBody = body,
                };

        await dispatcher.DispatchAsync(workflowEvent, cancellationToken).ConfigureAwait(false);

        return new AcceptedResult(string.Empty, new { correlationId });
    }

    private ShipmentWorkflowEvent BuildCommandEvent(string body, string correlationId)
    {
        var envelope = envelopeFactory.Create(body);

        var command = envelope.Command.OperationType switch
        {
            OperationType.SubmitShipment => (CommandBase?)
                envelope.Deserialize<SubmitShipmentCommand>(),
            OperationType.UpdateShipmentStatus => envelope.Deserialize<UpdateShipmentStatusCommand>(),
            _ => envelope.Command,
        };

        return new ShipmentWorkflowEvent
        {
            EventType = WorkflowEventType.ShipmentCommand,
            CorrelationId = correlationId,
            Command = command,
            RawBody = body,
        };
    }

    /// <summary>
    /// Both sides are hashed and compared in fixed time: a byte-by-byte comparison of the raw key
    /// leaks where the first mismatch is, which is enough to recover it one character at a time.
    /// </summary>
    internal static bool IsAuthorized(HttpRequest request, string expectedKey)
    {
        var provided = request.Headers["x-api-key"].ToString();

        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expectedKey))
        {
            return false;
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));

        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
}
