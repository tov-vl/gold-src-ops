namespace GoldSrcOps.Application.Commands;

public enum RconCommandExecutionResultKind
{
    Succeeded = 1,
    Failed = 2,
    TimedOut = 3,
    AuthenticationFailed = 4
}

public sealed record RconCommandExecutionResult(
    RconCommandExecutionResultKind Kind,
    string? ResultSummary,
    string? FailureReason)
{
    public static RconCommandExecutionResult Succeeded(string? resultSummary = null) =>
        new(RconCommandExecutionResultKind.Succeeded, resultSummary, FailureReason: null);

    public static RconCommandExecutionResult Failed(string? failureReason = null) =>
        new(RconCommandExecutionResultKind.Failed, ResultSummary: null, failureReason);

    public static RconCommandExecutionResult TimedOut(string? failureReason = null) =>
        new(RconCommandExecutionResultKind.TimedOut, ResultSummary: null, failureReason);

    public static RconCommandExecutionResult AuthenticationFailed(string? failureReason = null) =>
        new(RconCommandExecutionResultKind.AuthenticationFailed, ResultSummary: null, failureReason);
}
