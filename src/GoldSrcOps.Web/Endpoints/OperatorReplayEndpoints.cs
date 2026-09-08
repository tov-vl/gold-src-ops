using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorReplayEndpoints
{
    private const int MaxReasonLength = 500;

    public static IEndpointRouteBuilder MapOperatorReplayEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/dead-letters/{eventId:guid}/replay",
                ReplayDeadLetterAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static async Task<IResult> ReplayDeadLetterAsync(
        Guid eventId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorReplayConfirmationStore confirmations,
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

        ReplayFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new ReplayFormRequest(
                form["Reason"].ToString().Trim(),
                form["ConfirmationToken"].ToString(),
                form["RequestId"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (request.Reason.Length == 0 ||
            request.Reason.Length > MaxReasonLength ||
            !request.Confirmed ||
            !Guid.TryParseExact(request.RequestId, "D", out var requestId) ||
            requestId == Guid.Empty)
        {
            return RedirectToDeadLetter(eventId, "invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(
                request.ConfirmationToken,
                subject,
                eventId,
                requestId))
        {
            return RedirectToDeadLetter(eventId, "confirmation-expired");
        }

        try
        {
            var result = await operatorApiClient.ReplayDeadLetterAsync(
                eventId,
                requestId,
                request.Reason,
                cancellationToken);

            return result switch
            {
                OperatorReplayResult.Accepted => RedirectToReplay(requestId, "accepted"),
                OperatorReplayResult.EventNotFound => RedirectToDeadLetter(eventId, "event-not-found"),
                OperatorReplayResult.Conflict => RedirectToDeadLetter(eventId, "conflict"),
                OperatorReplayResult.Rejected => RedirectToDeadLetter(eventId, "rejected"),
                _ => throw new InvalidOperationException($"Unsupported replay result '{result}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToReplay(requestId, "unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToReplay(requestId, "unknown");
        }
    }

    private static IResult RedirectToDeadLetter(Guid eventId, string result) =>
        Results.LocalRedirect(AddResult($"/operator/dead-letters/{eventId:D}", result));

    private static IResult RedirectToReplay(Guid requestId, string result) =>
        Results.LocalRedirect(AddResult($"/operator/replays/{requestId:D}", result));

    private static string AddResult(string path, string result) =>
        QueryHelpers.AddQueryString(path, "result", result);

    private sealed record ReplayFormRequest(
        string Reason,
        string ConfirmationToken,
        string RequestId,
        bool Confirmed);
}
