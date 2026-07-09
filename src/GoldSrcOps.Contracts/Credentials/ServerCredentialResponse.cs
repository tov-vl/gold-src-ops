namespace GoldSrcOps.Contracts.Credentials;

public sealed record ServerCredentialResponse(
    Guid Id,
    Guid ServerId,
    string Kind,
    bool IsConfigured,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
