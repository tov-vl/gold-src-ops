using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Credentials;

public sealed record SetServerCredentialCommand(
    ServerCredentialKind Kind,
    string SecretReference);
