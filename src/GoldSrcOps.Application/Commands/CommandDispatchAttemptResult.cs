using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Commands;

public enum CommandDispatchAttemptResultKind
{
    NoCommand = 1,
    Completed = 2,
    CompletionLost = 3
}

public sealed record CommandDispatchAttemptResult(
    CommandDispatchAttemptResultKind Kind,
    Guid? CommandId,
    Guid? ServerId,
    CommandExecutionStatus? Status)
{
    public static CommandDispatchAttemptResult NoCommand() =>
        new(CommandDispatchAttemptResultKind.NoCommand, CommandId: null, ServerId: null, Status: null);

    public static CommandDispatchAttemptResult Completed(CommandExecution command) =>
        FromCommand(CommandDispatchAttemptResultKind.Completed, command);

    public static CommandDispatchAttemptResult CompletionLost(CommandExecution command) =>
        FromCommand(CommandDispatchAttemptResultKind.CompletionLost, command);

    private static CommandDispatchAttemptResult FromCommand(
        CommandDispatchAttemptResultKind kind,
        CommandExecution command) =>
        new(kind, command.Id, command.ServerId, command.Status);
}
