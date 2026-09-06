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
}
