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

    public async Task<SetServerCredentialResult> SetAsync(
        Guid serverId,
        SetServerCredentialCommand command,
        CancellationToken cancellationToken)
    {
        var server = await _credentials.GetServerForUpdateAsync(serverId, cancellationToken);
        if (server is null)
        {
            return SetServerCredentialResult.NotFound();
        }

        if (server.Revision != command.ExpectedServerRevision)
        {
            return SetServerCredentialResult.RevisionConflict();
        }

        if (server.IsEnabled)
        {
            return SetServerCredentialResult.MonitoringEnabled();
        }

        if (await _credentials.HasIncompleteCommandsAsync(serverId, cancellationToken))
        {
            return SetServerCredentialResult.CommandsInProgress();
        }

        var secretReference = RconSecretReference.Create(command.SecretAlias);
        var credential = await _credentials.GetAsync(serverId, command.Kind, cancellationToken);
        if (credential is null)
        {
            if (command.ExpectedCredentialRevision != 0)
            {
                return SetServerCredentialResult.RevisionConflict();
            }

            credential = new ServerCredential(serverId, command.Kind, secretReference, _clock.UtcNow);
            await _credentials.AddAsync(credential, cancellationToken);
        }
        else
        {
            if (credential.Revision != command.ExpectedCredentialRevision)
            {
                return SetServerCredentialResult.RevisionConflict();
            }

            credential.UpdateSecretReference(secretReference, _clock.UtcNow);
        }

        server.RecordCredentialChange();
        if (!await _credentials.TrySaveChangesAsync(cancellationToken))
        {
            return SetServerCredentialResult.RevisionConflict();
        }

        return SetServerCredentialResult.Updated(Map(credential));
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
            credential.Revision,
            credential.Kind,
            credential.IsConfigured,
            credential.CreatedAtUtc,
            credential.UpdatedAtUtc);
}
