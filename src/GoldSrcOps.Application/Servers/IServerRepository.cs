using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public interface IServerRepository
{
    Task AddAsync(Server server, CancellationToken cancellationToken);

    Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Server>> ListEnabledAsync(CancellationToken cancellationToken);

    Task AddSnapshotAsync(PollSnapshot snapshot, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
