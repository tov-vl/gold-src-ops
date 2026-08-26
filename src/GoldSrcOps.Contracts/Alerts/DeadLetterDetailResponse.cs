using System.Text.Json;

namespace GoldSrcOps.Contracts.Alerts;

public sealed record DeadLetterDetailResponse(
    Guid EventId,
    string EventType,
    short PayloadVersion,
    string AggregateType,
    Guid AggregateId,
    DateTimeOffset OccurredAtUtc,
    JsonElement Payload,
    int AttemptCount,
    int ReplayCount,
    DateTimeOffset? DeadLetteredAtUtc,
    string? LastError,
    bool HasNewerEvent,
    Guid? NewerEventId,
    string? NewerEventStatus,
    DateTimeOffset? LatestKnownOccurredAtUtc);
