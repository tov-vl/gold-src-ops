using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorServerUpdateEndpoints
{
    public static IEndpointRouteBuilder MapOperatorServerUpdateEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/servers/{serverId:guid}/settings/apply",
                UpdateAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static async Task<IResult> UpdateAsync(
        Guid serverId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorServerUpdateConfirmationStore confirmations,
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
            return RedirectToSettings(serverId, "invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(
                request.ConfirmationToken,
                subject,
                serverId,
                out var draft))
        {
            return RedirectToSettings(serverId, "confirmation-expired");
        }

        try
        {
            var result = await operatorApiClient.UpdateServerAsync(draft, cancellationToken);
            return result.Kind switch
            {
                OperatorServerUpdateResultKind.Updated => RedirectToSettings(serverId, "updated"),
                OperatorServerUpdateResultKind.ServerNotFound => RedirectToSettings(
                    serverId,
                    "server-not-found"),
                OperatorServerUpdateResultKind.Conflict => RedirectToSettings(serverId, "conflict"),
                OperatorServerUpdateResultKind.Rejected => RedirectToSettings(serverId, "rejected"),
                _ => throw new InvalidOperationException(
                    $"Unsupported server update result '{result.Kind}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToSettings(serverId, "update-unknown");
        }
        catch (InvalidDataException)
        {
            return RedirectToSettings(serverId, "update-unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToSettings(serverId, "update-unknown");
        }
    }

    private static IResult RedirectToSettings(Guid serverId, string result) =>
        Results.LocalRedirect(QueryHelpers.AddQueryString(
            $"/operator/servers/{serverId:D}/settings",
            "result",
            result));

    private sealed record UpdateFormRequest(
        string ConfirmationToken,
        bool Confirmed);
}
