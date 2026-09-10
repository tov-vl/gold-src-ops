using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorRconCredentialEndpoints
{
    public static IEndpointRouteBuilder MapOperatorRconCredentialEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/servers/{serverId:guid}/credentials/apply",
                UpdateAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        Guid serverId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorRconCredentialConfirmationStore confirmations,
        IOperatorApiClient operatorApiClient,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        UpdateFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new UpdateFormRequest(
                form["ConfirmationToken"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (!request.Confirmed)
        {
            return RedirectToCredentials(serverId, "invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(
                request.ConfirmationToken,
                subject,
                serverId,
                out var draft))
        {
            return RedirectToCredentials(serverId, "confirmation-expired");
        }

        try
        {
            var result = await operatorApiClient.SetRconCredentialAsync(draft, cancellationToken);
            return result.Kind switch
            {
                OperatorRconCredentialUpdateResultKind.Updated =>
                    RedirectToCredentials(serverId, "updated"),
                OperatorRconCredentialUpdateResultKind.ServerNotFound =>
                    RedirectToCredentials(serverId, "server-not-found"),
                OperatorRconCredentialUpdateResultKind.MonitoringEnabled =>
                    RedirectToCredentials(serverId, "monitoring-enabled"),
                OperatorRconCredentialUpdateResultKind.CommandsInProgress =>
                    RedirectToCredentials(serverId, "commands-in-progress"),
                OperatorRconCredentialUpdateResultKind.Conflict =>
                    RedirectToCredentials(serverId, "conflict"),
                OperatorRconCredentialUpdateResultKind.Rejected =>
                    RedirectToCredentials(serverId, "rejected"),
                _ => throw new InvalidOperationException(
                    $"Unsupported credential update result '{result.Kind}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToCredentials(serverId, "update-unknown");
        }
        catch (InvalidDataException)
        {
            return RedirectToCredentials(serverId, "update-unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToCredentials(serverId, "update-unknown");
        }
    }

    private static IResult RedirectToCredentials(Guid serverId, string result) =>
        Results.LocalRedirect(QueryHelpers.AddQueryString(
            $"/operator/servers/{serverId:D}/credentials",
            "result",
            result));

    private sealed record UpdateFormRequest(string ConfirmationToken, bool Confirmed);
}
