using System.Text;
using System.Text.Json;
using GoldSrcOps.Contracts.Monitoring;
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

    private static async Task<IResult> DownloadAsync(IGameHostHealthClient client, TimeProvider timeProvider,
        HttpContext context, CancellationToken cancellationToken)
    {
        GameHostHealthResponse? snapshot;
        try
        {
            snapshot = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        {
            snapshot = null;
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(Encoding.UTF8.GetBytes(GameHostHealthReport.Create(snapshot, timeProvider.GetUtcNow())),
            "text/plain; charset=utf-8", "goldsrcops-game-host-health.txt");
    }
}
