using System.Security.Cryptography;
using System.Text;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Commands;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal sealed class PublicPlayerSearchSource(
    PublicPlayerSearchSettings settings,
    IServerRepository servers,
    IServerCredentialRepository credentials,
    ISecretReferenceResolver secrets,
    IGoldSrcRconClient rcon,
    TimeProvider timeProvider) : IPublicPlayerSearchSource
{
    public async Task<PublicPlayerSearchResult> LookupAsync(string name, PublicPlayerSearchMode mode, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || !PublicPlayerSearchMatching.TryNormalize(name, mode, out var normalized) ||
            !string.Equals(name, normalized, StringComparison.Ordinal))
        {
            return PublicPlayerSearchResult.Unavailable;
        }

        var server = await servers.GetAsync(settings.ServerId, cancellationToken);
        if (server is not { IsEnabled: true, Endpoint.RconPort: not null })
        {
            return PublicPlayerSearchResult.Unavailable;
        }

        if (await credentials.HasIncompleteCommandsAsync(server.Id, cancellationToken))
        {
            return PublicPlayerSearchResult.Busy(10);
        }

        var credential = await credentials.GetAsync(server.Id, ServerCredentialKind.RconPassword, cancellationToken);
        if (credential is not { IsConfigured: true })
        {
            return PublicPlayerSearchResult.Unavailable;
        }

        var resolved = await secrets.ResolveAsync(credential.SecretReference, cancellationToken);
        if (resolved.Kind != SecretReferenceResolutionResultKind.Resolved || string.IsNullOrWhiteSpace(resolved.Secret))
        {
            return PublicPlayerSearchResult.Unavailable;
        }

        var command = mode == PublicPlayerSearchMode.Contains ? "goldsrcops_player_lookup_v2" : "goldsrcops_player_lookup";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(name));
        var response = await rcon.ExecuteAsync(new GoldSrcRconRequest(
            server.Endpoint.Host, server.Endpoint.RconPort.Value, resolved.Secret,
            $"{command} {nonce} {hex}", TimeSpan.FromSeconds(3)), cancellationToken);
        return PublicPlayerSearchProtocol.Parse(response, nonce, name, timeProvider.GetUtcNow(), mode);
    }
}
