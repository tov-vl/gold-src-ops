namespace GoldSrcOps.GameEventAgent;

internal sealed record GameEventSourceInput(
    string Type,
    DateTimeOffset OccurredAtUtc,
    string? Map,
    int? Players,
    int? Bots);

internal sealed record GameEventSpoolEnvelope(
    short SpoolVersion,
    Guid RecordId,
    GameEventSourceInput Event);

internal sealed record GameEventSpoolEnqueueResult(
    Guid EventId,
    long SequenceNumber,
    bool AlreadyQueued);

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
    int SpoolReceipts,
    long NextSequenceNumber);

internal enum GameEventQueueStatus
{
    Pending = 0,
    InFlight = 1,
    DeadLetter = 2
}
