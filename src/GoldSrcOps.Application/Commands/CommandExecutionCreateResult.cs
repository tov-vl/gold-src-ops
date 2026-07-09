namespace GoldSrcOps.Application.Commands;

public enum CommandExecutionCreateResultKind
{
    Created = 1,
    ServerNotFound = 2,
    MissingRconCredential = 3
}

public sealed record CommandExecutionCreateResult(
    CommandExecutionCreateResultKind Kind,
    CommandExecutionDto? Command)
{
    public static CommandExecutionCreateResult Created(CommandExecutionDto command) =>
        new(CommandExecutionCreateResultKind.Created, command);

    public static CommandExecutionCreateResult ServerNotFound() =>
        new(CommandExecutionCreateResultKind.ServerNotFound, Command: null);

    public static CommandExecutionCreateResult MissingRconCredential() =>
        new(CommandExecutionCreateResultKind.MissingRconCredential, Command: null);
}
