namespace GoldSrcOps.Contracts.Monitoring;

public sealed record CredentialExpiryResponse(DateTimeOffset EvaluatedAtUtc, IReadOnlyList<CredentialExpiryEntryResponse> Entries);
