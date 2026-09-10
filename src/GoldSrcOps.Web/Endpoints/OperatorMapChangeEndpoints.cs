using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorMapChangeEndpoints
{
    private const int CommandLimit = 100;

    public static IEndpointRouteBuilder MapOperatorMapChangeEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/servers/{serverId:guid}/commands/change-map/queue",
                QueueMapChangeAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static async Task<IResult> QueueMapChangeAsync(
        Guid serverId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorMapChangeConfirmationStore confirmations,
        IReaderApiClient readerApiClient,
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

        MapChangeFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new MapChangeFormRequest(
                form["ConfirmationToken"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (!request.Confirmed)
        {
            return RedirectToForm(serverId, "invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(
                request.ConfirmationToken,
                subject,
                serverId,
                out var draft))
        {
            return RedirectToForm(serverId, "confirmation-expired");
        }

        try
        {
            var commands = await readerApiClient.GetServerCommandsAsync(
                serverId,
                CommandLimit,
                cancellationToken);
            if (commands is null)
            {
                return RedirectToForm(serverId, "server-not-found");
            }

            if (commands.Any(IsIncomplete))
            {
                return RedirectToForm(serverId, "commands-in-progress");
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or InvalidDataException)
        {
            return RedirectToForm(serverId, "precondition-unavailable");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToForm(serverId, "precondition-unavailable");
        }

        try
        {
            var result = await operatorApiClient.QueueMapChangeAsync(
                serverId,
                draft.Map,
                cancellationToken);

            return result switch
            {
                OperatorCommandQueueResult.Queued => RedirectToHistory(serverId, "map-change-queued"),
                OperatorCommandQueueResult.ServerNotFound => RedirectToForm(serverId, "server-not-found"),
                OperatorCommandQueueResult.MissingRconCredential => RedirectToForm(serverId, "credential-missing"),
                OperatorCommandQueueResult.Rejected => RedirectToForm(serverId, "rejected"),
                _ => throw new InvalidOperationException($"Unsupported command result '{result}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToHistory(serverId, "map-change-unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToHistory(serverId, "map-change-unknown");
        }
    }

    private static bool IsIncomplete(CommandExecutionResponse command) =>
        string.Equals(command.Status, "Pending", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command.Status, "Running", StringComparison.OrdinalIgnoreCase);

    private static IResult RedirectToForm(Guid serverId, string result) =>
        Results.LocalRedirect(AddResult(
            $"/operator/servers/{serverId:D}/commands/change-map",
            result));

    private static IResult RedirectToHistory(Guid serverId, string result) =>
        Results.LocalRedirect(AddResult(
            $"/operator/servers/{serverId:D}/commands",
            result));

    private static string AddResult(string path, string result) =>
        QueryHelpers.AddQueryString(path, "result", result);

    private sealed record MapChangeFormRequest(
        string ConfirmationToken,
        bool Confirmed);
}
