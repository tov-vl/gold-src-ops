using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Incidents;

public sealed record AvailabilityIncidentDto(
    Guid Id,
    Guid ServerId,
    IncidentType Type,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string StartReason,
    string? EndReason,
    int ConsecutiveFailures);
