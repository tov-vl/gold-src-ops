using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.Web.Services;

internal interface IOperatorApiClient
{
    Task<OperatorServerRegistrationResult> RegisterServerAsync(
        OperatorServerRegistrationDraft draft,
        CancellationToken cancellationToken = default);

    Task<OperatorCommandQueueResult> QueueSayAsync(
        Guid serverId,
        string message,
        CancellationToken cancellationToken = default);

    Task<OperatorMonitoringUpdateResult> SetMonitoringEnabledAsync(
        Guid serverId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<OperatorServerUpdateResult> UpdateServerAsync(
        OperatorServerUpdateDraft draft,
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

internal enum OperatorMonitoringUpdateResult
{
    Updated,
    ServerNotFound,
    Conflict
}

internal enum OperatorServerUpdateResultKind
{
    Updated,
    ServerNotFound,
    Conflict,
    Rejected
}

internal sealed record OperatorServerUpdateResult(
    OperatorServerUpdateResultKind Kind,
    ServerResponse? Server);

internal enum OperatorServerRegistrationResultKind
{
    Created,
    Idempotent,
    Conflict,
    Rejected
}

internal sealed record OperatorServerRegistrationResult(
    OperatorServerRegistrationResultKind Kind,
    ServerResponse? Server);
