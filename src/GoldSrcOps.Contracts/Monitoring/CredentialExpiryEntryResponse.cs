namespace GoldSrcOps.Contracts.Monitoring;

public sealed record CredentialExpiryEntryResponse(string Key, string State, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? VerifiedAtUtc, int? RemainingDays);
