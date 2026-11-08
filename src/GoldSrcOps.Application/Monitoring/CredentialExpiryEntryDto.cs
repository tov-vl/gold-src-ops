namespace GoldSrcOps.Application.Monitoring;

public sealed record CredentialExpiryEntryDto(string Key, string State, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? VerifiedAtUtc, int? RemainingDays, string? ExpiresAtMinuteUtc = null);
