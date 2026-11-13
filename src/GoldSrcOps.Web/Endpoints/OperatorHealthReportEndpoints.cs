using System.Text;
using System.Text.Json;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorHealthReportEndpoints
{
    public const string Path = "/operator/game-host-health/report";

    public static IEndpointRouteBuilder MapOperatorHealthReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Path, DownloadAsync).RequireAuthorization(WebSecurity.OperatorPolicy);
        return endpoints;
    }

    private static async Task<IResult> DownloadAsync(IGameHostHealthClient client, IArchiveHealthClient archiveClient, IPollingHealthClient pollingClient,
        ICredentialExpiryClient credentialClient, IIncidentDeliveryHealthClient deliveryClient, IRetentionHealthClient retentionClient, ICommandDispatchHealthClient commandDispatchClient,
        TimeProvider timeProvider, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receiverConfigured = deliveryClient.IsReceiverConfigured;
        var gameHostRead = ReadSnapshotAsync(client.ReadAsync, cancellationToken);
        var archiveRead = ReadSnapshotAsync(archiveClient.ReadAsync, cancellationToken);
        var pollingRead = ReadSnapshotAsync(pollingClient.ReadAsync, cancellationToken);
        var credentialRead = ReadSnapshotAsync(credentialClient.ReadAsync, cancellationToken);
        var retentionRead = ReadSnapshotAsync(retentionClient.ReadAsync, cancellationToken);
        var commandDispatchRead = ReadSnapshotAsync(commandDispatchClient.ReadAsync, cancellationToken);
        var outboxRead = ReadSnapshotAsync(deliveryClient.ReadOutboxAsync, cancellationToken);
        var receiverRead = receiverConfigured ? ReadSnapshotAsync(deliveryClient.ReadReceiverAsync, cancellationToken) :
            Task.FromResult<ProviderDeliveryStatusResponse?>(null);
        await Task.WhenAll(gameHostRead, archiveRead, pollingRead, credentialRead, outboxRead, receiverRead, retentionRead, commandDispatchRead).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var supplement = new SupplementalHealthReport.Snapshots(await credentialRead.ConfigureAwait(false), await outboxRead.ConfigureAwait(false),
            await receiverRead.ConfigureAwait(false), receiverConfigured);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(Encoding.UTF8.GetBytes(GameHostHealthReport.Create(
                await gameHostRead.ConfigureAwait(false), timeProvider.GetUtcNow(), await archiveRead.ConfigureAwait(false), await pollingRead.ConfigureAwait(false),
                supplement, await retentionRead.ConfigureAwait(false), await commandDispatchRead.ConfigureAwait(false))),
            "text/plain; charset=utf-8", "goldsrcops-game-host-health.txt");
    }

    private static async Task<T?> ReadSnapshotAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException)
        {
            return null;
        }
    }
}
