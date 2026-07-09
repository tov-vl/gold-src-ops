using GoldSrcOps.Application.Common;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Credentials;

public sealed class ServerCredentialsService
{
    private readonly IServerCredentialRepository _credentials;
    private readonly IClock _clock;

    public ServerCredentialsService(IServerCredentialRepository credentials, IClock clock)
    {
        _credentials = credentials;
        _clock = clock;
    }

    public async Task<ServerCredentialDto?> SetAsync(
        Guid serverId,
        SetServerCredentialCommand command,
        CancellationToken cancellationToken)
    {
        if (!await _credentials.ServerExistsAsync(serverId, cancellationToken))
        {
            return null;
        }

        var credential = await _credentials.GetAsync(serverId, command.Kind, cancellationToken);
        if (credential is null)
        {
            credential = new ServerCredential(serverId, command.Kind, command.SecretReference, _clock.UtcNow);
            await _credentials.AddAsync(credential, cancellationToken);
        }
        else
        {
            credential.UpdateSecretReference(command.SecretReference, _clock.UtcNow);
        }

        await _credentials.SaveChangesAsync(cancellationToken);

        return Map(credential);
    }

    public async Task<IReadOnlyList<ServerCredentialDto>?> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        if (!await _credentials.ServerExistsAsync(serverId, cancellationToken))
        {
            return null;
        }

        var credentials = await _credentials.ListByServerAsync(serverId, cancellationToken);
        return credentials.Select(Map).ToArray();
    }

    private static ServerCredentialDto Map(ServerCredential credential) =>
        new(
            credential.Id,
            credential.ServerId,
            credential.Kind,
            credential.IsConfigured,
            credential.CreatedAtUtc,
            credential.UpdatedAtUtc);
}
