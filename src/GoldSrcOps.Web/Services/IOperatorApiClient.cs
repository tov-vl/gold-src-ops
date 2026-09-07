namespace GoldSrcOps.Web.Services;

internal interface IOperatorApiClient
{
    Task<OperatorCommandQueueResult> QueueSayAsync(
        Guid serverId,
        string message,
        CancellationToken cancellationToken = default);
}

internal enum OperatorCommandQueueResult
{
    Queued,
    ServerNotFound,
    MissingRconCredential,
    Rejected
}
