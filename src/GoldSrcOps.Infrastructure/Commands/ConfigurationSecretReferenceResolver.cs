using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Commands;

internal sealed class ConfigurationSecretReferenceResolver : ISecretReferenceResolver
{
    private const string EnvScheme = "env://";
    private const string ConfigScheme = "config://";
    private const string DevSecretsScheme = "dev-secrets://";
    private const string DevSecretsRoot = "DevSecrets";

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

        var normalized = secretReference.Trim();
        if (normalized.StartsWith(EnvScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ResolveEnvironmentVariable(normalized[EnvScheme.Length..]));
        }

        if (normalized.StartsWith(ConfigScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ResolveConfigurationKey(normalized[ConfigScheme.Length..]));
        }

        if (normalized.StartsWith(DevSecretsScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ResolveConfigurationKey(ToDevSecretsConfigurationKey(normalized[DevSecretsScheme.Length..])));
        }

        return Task.FromResult(SecretReferenceResolutionResult.Unsupported());
    }

    private static SecretReferenceResolutionResult ResolveEnvironmentVariable(string name)
    {
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return SecretReferenceResolutionResult.NotFound();
        }

        var value = Environment.GetEnvironmentVariable(normalizedName);
        return string.IsNullOrWhiteSpace(value)
            ? SecretReferenceResolutionResult.NotFound()
            : SecretReferenceResolutionResult.Resolved(value);
    }

    private SecretReferenceResolutionResult ResolveConfigurationKey(string key)
    {
        var normalizedKey = key.Trim().Trim(':');
        if (string.IsNullOrWhiteSpace(normalizedKey))
        {
            return SecretReferenceResolutionResult.NotFound();
        }

        var value = _configuration[normalizedKey];
        return string.IsNullOrWhiteSpace(value)
            ? SecretReferenceResolutionResult.NotFound()
            : SecretReferenceResolutionResult.Resolved(value);
    }

    private static string ToDevSecretsConfigurationKey(string path)
    {
        var normalizedPath = path
            .Trim()
            .Trim('/', '\\')
            .Replace('/', ':')
            .Replace('\\', ':');

        return $"{DevSecretsRoot}:{normalizedPath}";
    }
}
