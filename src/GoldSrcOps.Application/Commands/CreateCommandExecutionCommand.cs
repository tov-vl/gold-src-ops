using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Commands;

public sealed record CreateCommandExecutionCommand(
    ServerCommandType Type,
    string? Payload,
    string? RequestedBy);
