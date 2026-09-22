using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using Vantage.Freight.Hub.Configuration;
using Vantage.Freight.Hub.Infrastructure;
using Vantage.Freight.Hub.Repository;
using Vantage.Freight.Hub.Services;
using Vantage.Freight.Hub.Services.Callbacks;
using Vantage.Freight.Hub.Services.Handlers;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

// ---------------------------------------------------------------------------------------------
// Configuration. Validated at start-up: a worker missing a setting should fail to come up rather
// than fail on whichever message happens to need it first.
// ---------------------------------------------------------------------------------------------
builder
    .Services.AddOptions<FreightHubOptions>()
    .Bind(builder.Configuration.GetSection(FreightHubOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// One serializer configuration, shared. Enums are written by name everywhere — over HTTP, in the
// snapshot blob and in the lifecycle table — so a payload stored today still reads correctly after
// somebody appends an enum member.
builder.Services.AddSingleton(new JsonSerializerOptions(JsonSerializerDefaults.Web));

builder.Services.AddSingleton(TimeProvider.System);

// ---------------------------------------------------------------------------------------------
// Storage. Singletons: the clients are thread-safe, hold connection pools, and are expensive to
// rebuild per message.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<FreightHubOptions>>().Value;
    return new BlobServiceClient(options.StorageConnectionString).GetBlobContainerClient(
        options.SnapshotContainerName
    );
});

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<FreightHubOptions>>().Value;
    return new TableServiceClient(options.StorageConnectionString).GetTableClient(
        options.LifecycleTableName
    );
});

builder.Services.AddSingleton<IShipmentSnapshotRepository, BlobShipmentSnapshotRepository>();
builder.Services.AddSingleton<IShipmentLifecycleTable, ShipmentLifecycleTable>();
builder.Services.AddSingleton<ICommandEnvelopeFactory, CommandEnvelopeFactory>();

// ---------------------------------------------------------------------------------------------
// Outbound HTTP. The forwarder is the only external dependency, and it goes through the factory
// with a retry policy — there is no second client allowed to skip one.
// ---------------------------------------------------------------------------------------------
builder
    .Services.AddHttpClient<IForwarderClient, ForwarderClient>(
        (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<FreightHubOptions>>().Value;

            client.BaseAddress = new Uri(options.ForwarderBaseUrl.TrimEnd('/') + '/');
            client.Timeout = TimeSpan.FromSeconds(options.ForwarderTimeoutSeconds);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    options.ForwarderApiToken
                );
        }
    )
    .AddTransientHttpErrorPolicy(policy =>
        policy.WaitAndRetryAsync(5, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)))
    );

// ---------------------------------------------------------------------------------------------
// The workflow. Handler registration order IS priority order — the dispatcher takes the first
// handler whose guard claims an event, so these lines are behaviour, not bookkeeping.
// ---------------------------------------------------------------------------------------------
builder.Services.AddSingleton<ShipmentStateWriter>();
builder.Services.AddSingleton<BookingSubmissionStep>();

builder.Services.AddSingleton<IShipmentStateHandler, SubmitShipmentHandler>();
builder.Services.AddSingleton<IShipmentStateHandler, DeliverySubmissionHandler>();
// Immediately after the submission handler: its guard is the exact inverse, so between them every
// delivery event is claimed exactly once. Moving this line above the previous one would park every
// delivery instead of sending it.
builder.Services.AddSingleton<IShipmentStateHandler, DeferredDeliveryHandler>();
builder.Services.AddSingleton<IShipmentStateHandler, CancellationHandler>();

builder.Services.AddSingleton<IForwarderCallbackHandler, AccountCallbackHandler>();
builder.Services.AddSingleton<IForwarderCallbackHandler, BookingCallbackHandler>();
builder.Services.AddSingleton<IForwarderCallbackHandler, PickupCallbackHandler>();
builder.Services.AddSingleton<IForwarderCallbackHandler, DeliveryCallbackHandler>();

builder.Services.AddSingleton<IShipmentWorkflowDispatcher, ShipmentWorkflowDispatcher>();

await builder.Build().RunAsync().ConfigureAwait(false);
