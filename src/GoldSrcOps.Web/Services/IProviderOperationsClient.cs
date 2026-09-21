using GoldSrcOps.Contracts.ProviderDelivery;

namespace GoldSrcOps.Web.Services;

internal interface IProviderOperationsClient
{
    bool IsEnabled { get; }

    Task<ProviderDeadLetterListResponse> GetDeadLettersAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ProviderDeadLetterListItemResponse?> GetDeadLetterAsync(
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task<ProviderDeadLetterReviewResult> ReviewDeadLetterAsync(
        Guid messageId,
        Guid requestId,
        string requestedBy,
        string reason,
        CancellationToken cancellationToken = default);

    Task<ProviderDeadLetterReviewResponse?> GetReviewAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);
}

internal enum ProviderDeadLetterReviewResultKind
{
    Accepted,
    Idempotent,
    NotFound,
    NotDeadLetter,
    IdempotencyConflict,
    AlreadyReviewed,
    Rejected,
}

internal sealed record ProviderDeadLetterReviewResult(
    ProviderDeadLetterReviewResultKind Kind,
    ProviderDeadLetterReviewResponse? Review = null);
