namespace GoldSrcOps.Contracts.Incidents;

public sealed record AvailabilityIncidentResponse(
    Guid Id,
    Guid ServerId,
    string Type,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string StartReason,
    string? EndReason,
    int ConsecutiveFailures);
