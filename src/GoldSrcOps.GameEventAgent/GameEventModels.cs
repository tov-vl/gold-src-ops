namespace GoldSrcOps.GameEventAgent;

internal sealed record GameEventSourceInput(
    string Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots);

internal sealed record QueuedGameEvent(
    Guid EventId,
    Guid SourceInstanceId,
    long SequenceNumber,
    short ContractVersion,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots,
    byte[] PayloadUtf8,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount);

internal sealed record GameEventQueueState(Guid SourceInstanceId, long NextSequenceNumber);

internal sealed record GameEventQueueStatistics(
    int Pending,
    int InFlight,
    int DeadLetter,
    long NextSequenceNumber);

internal enum GameEventQueueStatus
{
    Pending = 0,
    InFlight = 1,
    DeadLetter = 2
}
