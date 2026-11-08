using System.Globalization;
using GoldSrcOps.Application.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Monitoring;

public sealed class ConfiguredCredentialExpirySource(IConfiguration configuration) : ICredentialExpirySource
{
    public IReadOnlyList<CredentialExpiryMetadata> Read() => [.. CredentialExpiryService.Keys.Select(key =>
        new CredentialExpiryMetadata(key, ReadDate(key, "ExpiresAtUtc"), ReadDate(key, "VerifiedAtUtc")))];

    private DateTimeOffset? ReadDate(string key, string field)
    {
        var section = key.Replace("-", string.Empty, StringComparison.Ordinal);
        var value = configuration[$"CredentialExpiry:{section}:{field}"];
        return value is { Length: 20 } && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }
}
