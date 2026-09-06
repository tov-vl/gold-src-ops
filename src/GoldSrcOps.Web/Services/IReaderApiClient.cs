using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;

namespace GoldSrcOps.Web.Services;

public interface IReaderApiClient
{
    Task<DashboardOverviewResponse> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerResponse>> GetServersAsync(CancellationToken cancellationToken = default);

    Task<ServerResponse?> GetServerAsync(Guid serverId, CancellationToken cancellationToken = default);

    Task<ServerStatusResponse?> GetServerStatusAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailabilityIncidentResponse>> GetOpenIncidentsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AvailabilityIncidentResponse>> GetServerIncidentsAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SnapshotHistoryResponse?> GetServerSnapshotsAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CommandExecutionResponse>?> GetServerCommandsAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<DeadLetterListResponse> GetDeadLettersAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default);

    Task<DeadLetterDetailResponse?> GetDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);
}
