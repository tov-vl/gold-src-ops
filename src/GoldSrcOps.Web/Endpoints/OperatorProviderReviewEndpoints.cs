using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorProviderReviewEndpoints
{
    private const int MaxRequestedByLength = 200;
    private const int MaxReasonLength = 500;

    public static IEndpointRouteBuilder MapOperatorProviderReviewEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/operator/provider-delivery/dead-letters/{messageId:guid}/review",
                ReviewDeadLetterAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);
        return endpoints;
    }

    private static async Task<IResult> ReviewDeadLetterAsync(
        Guid messageId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorProviderReviewConfirmationStore confirmations,
        IProviderOperationsClient providerOperationsClient,
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

        if (!providerOperationsClient.IsEnabled || !context.Request.HasFormContentType)
        {
            return RedirectToMessage(messageId, "unavailable");
        }

        ReviewFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new ReviewFormRequest(
                form["Reason"].ToString().Trim(),
                form["ConfirmationToken"].ToString(),
                form["RequestId"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (request.Reason.Length is 0 or > MaxReasonLength ||
            subject is null || subject.Length > MaxRequestedByLength ||
            !request.Confirmed ||
            !Guid.TryParseExact(request.RequestId, "D", out var requestId) ||
            requestId == Guid.Empty)
        {
            return RedirectToMessage(messageId, "invalid");
        }

        if (!confirmations.TryConsume(
                request.ConfirmationToken,
                subject,
                messageId,
                requestId))
        {
            return RedirectToMessage(messageId, "confirmation-expired");
        }

        try
        {
            var result = await providerOperationsClient.ReviewDeadLetterAsync(
                messageId,
                requestId,
                subject,
                request.Reason,
                cancellationToken);
            return result.Kind switch
            {
                ProviderDeadLetterReviewResultKind.Accepted =>
                    RedirectToReview(requestId, "accepted"),
                ProviderDeadLetterReviewResultKind.Idempotent =>
                    RedirectToReview(requestId, "idempotent"),
                ProviderDeadLetterReviewResultKind.NotFound =>
                    RedirectToMessage(messageId, "not-found"),
                ProviderDeadLetterReviewResultKind.NotDeadLetter =>
                    RedirectToMessage(messageId, "not-dead-letter"),
                ProviderDeadLetterReviewResultKind.IdempotencyConflict =>
                    RedirectToMessage(messageId, "conflict"),
                ProviderDeadLetterReviewResultKind.AlreadyReviewed =>
                    RedirectToMessage(messageId, "already-reviewed"),
                ProviderDeadLetterReviewResultKind.Rejected =>
                    RedirectToMessage(messageId, "rejected"),
                _ => throw new InvalidOperationException(
                    $"Unsupported provider review result '{result.Kind}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToReview(requestId, "unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToReview(requestId, "unknown");
        }
    }

    private static IResult RedirectToMessage(Guid messageId, string result) =>
        Results.LocalRedirect(QueryHelpers.AddQueryString(
            $"/operator/provider-delivery/dead-letters/{messageId:D}",
            "result",
            result));

    private static IResult RedirectToReview(Guid requestId, string result) =>
        Results.LocalRedirect(QueryHelpers.AddQueryString(
            $"/operator/provider-delivery/reviews/{requestId:D}",
            "result",
            result));

    private sealed record ReviewFormRequest(
        string Reason,
        string ConfirmationToken,
        string RequestId,
        bool Confirmed);
}
