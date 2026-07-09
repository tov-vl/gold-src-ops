using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Credentials;

public interface IServerCredentialRepository
{
    Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken);

    Task AddAsync(ServerCredential credential, CancellationToken cancellationToken);

    Task<ServerCredential?> GetAsync(
        Guid serverId,
        ServerCredentialKind kind,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ServerCredential>> ListByServerAsync(
        Guid serverId,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
