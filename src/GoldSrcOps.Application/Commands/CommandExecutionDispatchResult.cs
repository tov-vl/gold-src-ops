namespace GoldSrcOps.Application.Commands;

public enum CommandExecutionDispatchResultKind
{
    Dispatched = 1,
    CommandNotFound = 2,
    NotPending = 3
}

public sealed record CommandExecutionDispatchResult(
    CommandExecutionDispatchResultKind Kind,
    CommandExecutionDto? Command)
{
    public static CommandExecutionDispatchResult Dispatched(CommandExecutionDto command) =>
        new(CommandExecutionDispatchResultKind.Dispatched, command);

    public static CommandExecutionDispatchResult CommandNotFound() =>
        new(CommandExecutionDispatchResultKind.CommandNotFound, Command: null);

    public static CommandExecutionDispatchResult NotPending(CommandExecutionDto command) =>
        new(CommandExecutionDispatchResultKind.NotPending, command);
}
