namespace GoldSrcOps.Application.Monitoring;

public sealed record CredentialExpiryDto(DateTimeOffset EvaluatedAtUtc, IReadOnlyList<CredentialExpiryEntryDto> Entries);
