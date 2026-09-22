using System.ComponentModel.DataAnnotations;

namespace Vantage.Freight.Hub.Configuration;

/// <summary>
/// Everything this service needs to run, in one validated shape.
/// </summary>
/// <remarks>
/// Bound once and validated at start-up rather than read ad hoc from the environment. A worker that
/// is missing its storage connection should fail to start, loudly, instead of failing on the first
/// message it happens to receive — by which time the message has been retried and dead-lettered and
/// the cause is three log queries away.
/// </remarks>
public sealed class FreightHubOptions
{
    public const string SectionName = "FreightHub";

    [Required]
    public string StorageConnectionString { get; set; } = string.Empty;

    [Required]
    public string SnapshotContainerName { get; set; } = "shipments";

    [Required]
    public string LifecycleTableName { get; set; } = "shipmentlifecycle";

    [Required]
    [Url]
    public string ForwarderBaseUrl { get; set; } = string.Empty;

    [Required]
    public string ForwarderApiToken { get; set; } = string.Empty;

    [Range(1, 300)]
    public int ForwarderTimeoutSeconds { get; set; } = 60;

    [Range(1, 10)]
    public int ForwarderRetryCount { get; set; } = 5;

    /// <summary>
    /// Shared secret on the inbound dev endpoint. Compared in fixed time; see
    /// <see cref="Functions.DevEventTrigger"/>.
    /// </summary>
    [Required]
    public string DevEndpointApiKey { get; set; } = string.Empty;
}
