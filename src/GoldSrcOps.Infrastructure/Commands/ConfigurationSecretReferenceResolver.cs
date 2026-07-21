using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class ConfigurationSecretReferenceResolver : ISecretReferenceResolver
{
    private const string RconSecretsRoot = "RconSecrets";

    private readonly IConfiguration _configuration;

    public ConfigurationSecretReferenceResolver(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<SecretReferenceResolutionResult> ResolveAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!GoldSrcOps.Domain.Servers.RconSecretReference.TryGetAlias(secretReference, out var secretAlias))
        {
            return Task.FromResult(SecretReferenceResolutionResult.Unsupported());
        }

        var value = _configuration[$"{RconSecretsRoot}:{secretAlias}"];
        return Task.FromResult(
            string.IsNullOrWhiteSpace(value)
                ? SecretReferenceResolutionResult.NotFound()
                : SecretReferenceResolutionResult.Resolved(value));
    }
}
