using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Commands;

public sealed record CommandExecutionDispatchContext(
    CommandExecution Command,
    string Host,
    int? RconPort,
    string? CredentialSecretReference);
