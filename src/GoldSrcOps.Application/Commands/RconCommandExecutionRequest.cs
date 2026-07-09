using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Commands;

public sealed record RconCommandExecutionRequest(
    Guid CommandId,
    Guid ServerId,
    string Host,
    int Port,
    string CredentialSecretReference,
    ServerCommandType Type,
    string CommandText);
