using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Persistence;
using GoldSrcOps.Contracts.ProviderDelivery;
using Microsoft.AspNetCore.Mvc;

namespace GoldSrcOps.AlertReceiver.ProviderOperations;

internal static class ProviderDeliveryOperationsEndpoints
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const long MaxRequestBodyBytes = 4 * 1024;

    public static IEndpointRouteBuilder MapProviderDeliveryOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/internal/v1/provider-delivery")
            .WithTags("Provider Delivery Operations");

        group.MapGet("/dead-letters", ListDeadLettersAsync)
            .WithName("ListProviderDeadLetters");
        group.MapGet("/dead-letters/{messageId:guid}", GetDeadLetterAsync)
            .WithName("GetProviderDeadLetter");
        group.MapPost("/dead-letters/{messageId:guid}/review", ReviewDeadLetterAsync)
            .WithName("ReviewProviderDeadLetter")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes));
        group.MapGet("/reviews/{requestId:guid}", GetReviewAsync)
            .WithName("GetProviderDeadLetterReview");

        return endpoints;
    }

    private static async Task<IResult> ListDeadLettersAsync(
        HttpRequest httpRequest,
        string? cursor,
        int? limit,
        ProviderOperationsAuthorization authorization,
        ProviderDeliveryOperationsService operations,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpRequest, authorization);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        ProviderDeadLetterPagePosition? position = null;
        if (cursor is not null && !ProviderDeadLetterCursor.TryDecode(cursor, out position))
        {
            return ValidationProblem("cursor", "Cursor is invalid or no longer supported.");
        }

        if (limit is < 1 or > ProviderDeliveryOperationsService.MaxDeadLetterLimit)
        {
            return ValidationProblem(
                "limit",
                $"Limit must be between 1 and {ProviderDeliveryOperationsService.MaxDeadLetterLimit}.");
        }

        var page = await operations.ListDeadLettersAsync(position, limit, cancellationToken);
        return Results.Ok(new ProviderDeadLetterListResponse(
            page.Limit,
            page.NextPosition is null ? null : ProviderDeadLetterCursor.Encode(page.NextPosition),
            page.Items));
    }

    private static async Task<IResult> GetDeadLetterAsync(
        HttpRequest httpRequest,
        Guid messageId,
        ProviderOperationsAuthorization authorization,
        ProviderDeliveryOperationsService operations,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpRequest, authorization);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var deadLetter = await operations.GetDeadLetterAsync(messageId, cancellationToken);
        return deadLetter is null ? Results.NotFound() : Results.Ok(deadLetter);
    }

    private static async Task<IResult> ReviewDeadLetterAsync(
        HttpRequest httpRequest,
        Guid messageId,
        ProviderDeadLetterReviewRequest request,
        ProviderOperationsAuthorization authorization,
        ProviderDeliveryOperationsService operations,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpRequest, authorization);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var idempotencyValues = httpRequest.Headers[IdempotencyKeyHeaderName];
        var requestId = Guid.Empty;
        if (idempotencyValues.Count != 1 ||
            !Guid.TryParseExact(idempotencyValues[0], "D", out requestId) ||
            requestId == Guid.Empty)
        {
            errors.Add(
                IdempotencyKeyHeaderName,
                ["A single non-empty canonical UUID Idempotency-Key header is required."]);
        }

        if (!TryNormalize(
                request.RequestedBy,
                ProviderOutboxReview.MaxRequestedByLength,
                out var requestedBy))
        {
            errors.Add(
                "requestedBy",
                [$"RequestedBy must contain between 1 and {ProviderOutboxReview.MaxRequestedByLength} characters after trimming."]);
        }

        if (!TryNormalize(request.Reason, ProviderOutboxReview.MaxReasonLength, out var reason))
        {
            errors.Add(
                "reason",
                [$"Reason must contain between 1 and {ProviderOutboxReview.MaxReasonLength} characters after trimming."]);
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await operations.ReviewDeadLetterAsync(
            new ProviderDeadLetterReviewCommand(
                requestId,
                messageId,
                requestedBy,
                reason),
            cancellationToken);

        return result.Kind switch
        {
            ProviderDeadLetterReviewResultKind.Accepted => Results.Accepted(
                $"/internal/v1/provider-delivery/reviews/{requestId:D}",
                result.Review),
            ProviderDeadLetterReviewResultKind.Idempotent => Results.Ok(result.Review),
            ProviderDeadLetterReviewResultKind.NotFound => Results.NotFound(),
            ProviderDeadLetterReviewResultKind.NotDeadLetter => Problem(
                "Provider outbox message is not dead-lettered.",
                "provider_delivery.message_not_dead_letter"),
            ProviderDeadLetterReviewResultKind.Conflict => Problem(
                "Idempotency-Key was already used for a different review.",
                "provider_delivery.idempotency_conflict"),
            ProviderDeadLetterReviewResultKind.AlreadyReviewed => Problem(
                "Provider dead letter already has a review record.",
                "provider_delivery.already_reviewed"),
            _ => throw new InvalidOperationException(
                $"Unexpected provider review result '{result.Kind}'."),
        };
    }

    private static async Task<IResult> GetReviewAsync(
        HttpRequest httpRequest,
        Guid requestId,
        ProviderOperationsAuthorization authorization,
        ProviderDeliveryOperationsService operations,
        CancellationToken cancellationToken)
    {
        var authorizationFailure = Authorize(httpRequest, authorization);
        if (authorizationFailure is not null)
        {
            return authorizationFailure;
        }

        var review = await operations.GetReviewAsync(requestId, cancellationToken);
        return review is null ? Results.NotFound() : Results.Ok(review);
    }

    private static IResult? Authorize(
        HttpRequest request,
        ProviderOperationsAuthorization authorization)
    {
        if (!authorization.IsEnabled)
        {
            return Results.NotFound();
        }

        return authorization.IsAuthorized(request.Headers.Authorization)
            ? null
            : Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Provider operations authorization failed.");
    }

    private static bool TryNormalize(string? value, int maxLength, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 && normalized.Length <= maxLength;
    }

    private static IResult ValidationProblem(string name, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [name] = [message],
        });

    private static IResult Problem(string title, string code) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: title,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
            });
}
