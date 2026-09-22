using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace Vantage.Freight.Hub.Tests;

/// <summary>
/// Starts a real Azurite process — blob and table — for the repository tests.
/// </summary>
/// <remarks>
/// The dual-persistence design is the part of this service most worth testing, and the things that
/// actually go wrong in it are service behaviours a fake does not have: blob names are flat,
/// case-sensitive strings; a prefix listing returns what the service decides it returns; table
/// keys compare case-sensitively; DateTimeOffset comes back normalized to UTC. An in-memory
/// dictionary agrees with whatever the code does, which makes it worthless as evidence here.
/// </remarks>
public sealed class AzuriteFixture : IAsyncLifetime, IDisposable
{
    /// <summary>
    /// Azurite's well-known development account. Not a secret: the emulator accepts only this
    /// account, and the same key ships in the Azure SDK's own samples.
    /// </summary>
    private const string EmulatorAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly StringBuilder output = new();
    private Process? process;
    private string dataDirectory = string.Empty;

    public string ConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var blobPort = GetFreeTcpPort();
        var tablePort = GetFreeTcpPort();
        this.dataDirectory = Directory.CreateTempSubdirectory("vantage-freight-azurite-").FullName;

        this.ConnectionString = string.Create(
            CultureInfo.InvariantCulture,
            $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;"
                + $"AccountKey={EmulatorAccountKey};"
                + $"BlobEndpoint=http://127.0.0.1:{blobPort}/devstoreaccount1;"
                + $"TableEndpoint=http://127.0.0.1:{tablePort}/devstoreaccount1;"
        );

        var startInfo = new ProcessStartInfo("azurite")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--blobHost");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--blobPort");
        startInfo.ArgumentList.Add(blobPort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--tableHost");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--tablePort");
        startInfo.ArgumentList.Add(tablePort.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--queuePort");
        startInfo.ArgumentList.Add(GetFreeTcpPort().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--location");
        startInfo.ArgumentList.Add(this.dataDirectory);
        // The Storage SDK negotiates a newer REST API version than the installed emulator knows.
        // Skipping the check is the documented way to run a current SDK against Azurite; the
        // operations this service uses have not changed between those versions.
        startInfo.ArgumentList.Add("--skipApiVersionCheck");
        startInfo.ArgumentList.Add("--silent");

        try
        {
            this.process = new Process { StartInfo = startInfo };

            // Both pipes are drained asynchronously: a full pipe buffer would block the emulator,
            // and the captured text is what a start-up failure gets reported with.
            this.process.OutputDataReceived += (_, args) => this.output.AppendLine(args.Data);
            this.process.ErrorDataReceived += (_, args) => this.output.AppendLine(args.Data);

            this.process.Start();
            this.process.BeginOutputReadLine();
            this.process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not start 'azurite'. Install it with 'npm install -g azurite'.",
                ex
            );
        }

        await this.WaitUntilReadyAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        this.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (this.process is { HasExited: false })
        {
            this.process.Kill(entireProcessTree: true);
            this.process.WaitForExit(5_000);
        }

        this.process?.Dispose();
        this.process = null;

        if (Directory.Exists(this.dataDirectory))
        {
            Directory.Delete(this.dataDirectory, recursive: true);
        }
    }

    public BlobContainerClient CreateContainer(string name) =>
        new BlobServiceClient(this.ConnectionString).GetBlobContainerClient(name);

    public TableClient CreateTable(string name) =>
        new TableServiceClient(this.ConnectionString).GetTableClient(name);

    /// <summary>Probes both endpoints until they answer, or gives up after 20 seconds.</summary>
    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var blobProbe = new BlobServiceClient(this.ConnectionString).GetBlobContainerClient(
            "startupprobe"
        );
        var tableProbe = new TableServiceClient(this.ConnectionString).GetTableClient(
            "startupprobe"
        );
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await blobProbe.CreateIfNotExistsAsync().ConfigureAwait(false);
                await tableProbe.CreateIfNotExistsAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastError = ex;
                await Task.Delay(250).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            "Azurite did not become ready within 20 seconds. "
                + $"Exited: {this.process?.HasExited}. Output: {this.output}. "
                + $"Last probe error: {lastError?.Message}"
        );
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "azurite";
}
