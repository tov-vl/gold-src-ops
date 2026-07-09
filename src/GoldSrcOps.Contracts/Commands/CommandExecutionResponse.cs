namespace GoldSrcOps.Contracts.Commands;

public sealed record CommandExecutionResponse(
    Guid Id,
    Guid ServerId,
    string Type,
    string Status,
    string? Payload,
    string? RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultSummary,
    string? FailureReason);
