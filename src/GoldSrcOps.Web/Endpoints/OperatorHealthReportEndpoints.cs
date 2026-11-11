using System.Text;
using System.Text.Json;
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

    private static async Task<IResult> DownloadAsync(IGameHostHealthClient client, IArchiveHealthClient archiveClient,
        TimeProvider timeProvider, HttpContext context, CancellationToken cancellationToken)
    {
        var gameHostRead = ReadSnapshotAsync(client.ReadAsync, cancellationToken);
        var archiveRead = ReadSnapshotAsync(archiveClient.ReadAsync, cancellationToken);
        await Task.WhenAll(gameHostRead, archiveRead).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(Encoding.UTF8.GetBytes(GameHostHealthReport.Create(
                await gameHostRead.ConfigureAwait(false), timeProvider.GetUtcNow(), await archiveRead.ConfigureAwait(false))),
            "text/plain; charset=utf-8", "goldsrcops-game-host-health.txt");
    }

    private static async Task<T?> ReadSnapshotAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        {
            return null;
        }
    }
}
