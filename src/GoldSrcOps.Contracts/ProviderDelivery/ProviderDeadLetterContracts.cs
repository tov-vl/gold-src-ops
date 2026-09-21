namespace GoldSrcOps.Contracts.ProviderDelivery;

public sealed record ProviderDeadLetterListResponse(
    int Limit,
    string? NextCursor,
    IReadOnlyList<ProviderDeadLetterListItemResponse> Items);

public sealed record ProviderDeadLetterListItemResponse(
    Guid MessageId,
    Guid SourceEventId,
    Guid IncidentId,
    string Action,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount,
    DateTimeOffset DeadLetteredAtUtc,
    string? FailureSummary,
    ProviderDeadLetterReviewResponse? Review);

public sealed record ProviderDeadLetterReviewRequest(
    string RequestedBy,
    string Reason);

public sealed record ProviderDeadLetterReviewResponse(
    Guid RequestId,
    Guid MessageId,
    string RequestedBy,
    string Reason,
    DateTimeOffset ReviewedAtUtc);
