using System.Globalization;
using GoldSrcOps.Application.Monitoring;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.Infrastructure.Monitoring;

public sealed class ConfiguredCredentialExpirySource(IConfiguration configuration) : ICredentialExpirySource
{
    public IReadOnlyList<CredentialExpiryMetadata> Read() => [.. CredentialExpiryService.Keys.Select(ReadEntry)];

    private CredentialExpiryMetadata ReadEntry(string key)
    {
        var section = key.Replace("-", string.Empty, StringComparison.Ordinal);
        var seconds = configuration[$"CredentialExpiry:{section}:ExpiresAtUtc"];
        var minute = configuration[$"CredentialExpiry:{section}:ExpiresAtMinuteUtc"];
        var verified = ReadDate(key, "VerifiedAtUtc");
        var hasSeconds = !string.IsNullOrEmpty(seconds);
        var hasMinute = !string.IsNullOrEmpty(minute);
        if (hasSeconds && hasMinute || hasMinute && !CredentialExpiryMinute.TryParse(minute, out _))
        {
            return new CredentialExpiryMetadata(key, ExpiresAtUtc: null, verified);
        }

        return new CredentialExpiryMetadata(key, ReadDate(key, "ExpiresAtUtc"), verified, hasMinute ? minute : null);
    }

    private DateTimeOffset? ReadDate(string key, string field)
    {
        var section = key.Replace("-", string.Empty, StringComparison.Ordinal);
        var value = configuration[$"CredentialExpiry:{section}:{field}"];
        return value is { Length: 20 } && DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }
}
