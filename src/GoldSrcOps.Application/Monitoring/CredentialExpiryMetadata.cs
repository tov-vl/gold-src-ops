namespace GoldSrcOps.Application.Monitoring;

public sealed record CredentialExpiryMetadata(string Key, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? VerifiedAtUtc, string? ExpiresAtMinuteUtc = null);
