using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Commands;

public sealed record CommandExecutionDto(
    Guid Id,
    Guid ServerId,
    ServerCommandType Type,
    CommandExecutionStatus Status,
    string? Payload,
    string? RequestedBy,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultSummary,
    string? FailureReason);
