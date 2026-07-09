using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Commands;

public interface ICommandExecutionRepository
{
    Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken);

    Task<bool> HasCredentialAsync(
        Guid serverId,
        ServerCredentialKind kind,
        CancellationToken cancellationToken);

    Task AddAsync(CommandExecution command, CancellationToken cancellationToken);

    Task<CommandExecution?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<CommandExecution>> ListByServerAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
