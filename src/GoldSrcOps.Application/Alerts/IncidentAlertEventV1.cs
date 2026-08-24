namespace GoldSrcOps.Application.Alerts;

public sealed record IncidentAlertEventV1(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    Guid IncidentId,
    Guid ServerId,
    string ServerName,
    string Reason,
    int ConsecutiveFailures,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    long? DurationSeconds)
{
    public const short CurrentPayloadVersion = 1;

    public short PayloadVersion { get; } = CurrentPayloadVersion;
}
