using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Credentials;

public sealed record ServerCredentialDto(
    Guid Id,
    Guid ServerId,
    ServerCredentialKind Kind,
    bool IsConfigured,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
