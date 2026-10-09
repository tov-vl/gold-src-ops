using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Commands;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class PublicLeaderboardPoller(
    PublicLeaderboardSettings settings,
    IServerRepository servers,
    IServerCredentialRepository credentials,
    ISecretReferenceResolver secrets,
    IGoldSrcRconClient rcon,
    PublicLeaderboardStore store,
    IClock clock)
{
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled)
        {
            return;
        }

        var server = await servers.GetAsync(settings.ServerId, cancellationToken);
        if (server is not { IsEnabled: true, Endpoint.RconPort: not null })
        {
            store.RecordFailure(sourceUnavailable: true);
            return;
        }

        if (await credentials.HasIncompleteCommandsAsync(server.Id, cancellationToken))
        {
            store.RecordFailure();
            return;
        }

        var credential = await credentials.GetAsync(server.Id, ServerCredentialKind.RconPassword, cancellationToken);
        if (credential is not { IsConfigured: true })
        {
            store.RecordFailure(sourceUnavailable: true);
            return;
        }

        var resolved = await secrets.ResolveAsync(credential.SecretReference, cancellationToken);
        if (resolved.Kind != SecretReferenceResolutionResultKind.Resolved || string.IsNullOrWhiteSpace(resolved.Secret))
        {
            store.RecordFailure(sourceUnavailable: true);
            return;
        }

        var response = await rcon.ExecuteAsync(new GoldSrcRconRequest(
            server.Endpoint.Host, server.Endpoint.RconPort.Value, resolved.Secret,
            "goldsrcops_leaderboard_snapshot", TimeSpan.FromSeconds(3)), cancellationToken);
        var snapshot = PublicLeaderboardProtocol.Parse(response, clock.UtcNow);
        if (snapshot is null)
        {
            store.RecordFailure(sourceUnavailable: true);
        }
        else
        {
            store.Publish(snapshot);
        }
    }
}
