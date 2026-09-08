namespace GoldSrcOps.Web.Services;

internal interface IOperatorApiClient
{
    Task<OperatorCommandQueueResult> QueueSayAsync(
        Guid serverId,
        string message,
        CancellationToken cancellationToken = default);

    Task<OperatorReplayResult> ReplayDeadLetterAsync(
        Guid eventId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default);
}

internal enum OperatorReplayResult
{
    Accepted,
    EventNotFound,
    Conflict,
    Rejected
}

internal enum OperatorCommandQueueResult
{
    Queued,
    ServerNotFound,
    MissingRconCredential,
    Rejected
}
