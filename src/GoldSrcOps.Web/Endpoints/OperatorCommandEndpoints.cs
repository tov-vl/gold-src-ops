using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorCommandEndpoints
{
    private const int MaxSayMessageLength = 512;

    public static IEndpointRouteBuilder MapOperatorCommandEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/servers/{serverId:guid}/commands/say",
                QueueSayAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static async Task<IResult> QueueSayAsync(
        Guid serverId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorCommandConfirmationStore confirmations,
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

        SayCommandFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new SayCommandFormRequest(
                form["Message"].ToString().Trim(),
                form["ConfirmationToken"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (request.Message.Length == 0 ||
            request.Message.Length > MaxSayMessageLength ||
            !request.Confirmed)
        {
            return RedirectToForm(serverId, "invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(request.ConfirmationToken, subject, serverId))
        {
            return RedirectToForm(serverId, "confirmation-expired");
        }

        try
        {
            var result = await operatorApiClient.QueueSayAsync(
                serverId,
                request.Message,
                cancellationToken);

            return result switch
            {
                OperatorCommandQueueResult.Queued => RedirectToHistory(serverId, "queued"),
                OperatorCommandQueueResult.ServerNotFound => RedirectToForm(serverId, "server-not-found"),
                OperatorCommandQueueResult.MissingRconCredential => RedirectToForm(serverId, "credential-missing"),
                OperatorCommandQueueResult.Rejected => RedirectToForm(serverId, "rejected"),
                _ => throw new InvalidOperationException($"Unsupported command result '{result}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToHistory(serverId, "unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToHistory(serverId, "unknown");
        }
    }

    private static IResult RedirectToForm(Guid serverId, string result) =>
        Results.LocalRedirect(AddResult(
            $"/operator/servers/{serverId:D}/commands/new",
            result));

    private static IResult RedirectToHistory(Guid serverId, string result) =>
        Results.LocalRedirect(AddResult(
            $"/operator/servers/{serverId:D}/commands",
            result));

    private static string AddResult(string path, string result) =>
        QueryHelpers.AddQueryString(path, "result", result);

    private sealed record SayCommandFormRequest(
        string Message,
        string ConfirmationToken,
        bool Confirmed);
}
