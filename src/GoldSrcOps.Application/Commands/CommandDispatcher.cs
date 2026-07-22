using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.Application.Commands;

public sealed class CommandDispatcher
{
    public const string InterruptedFailureReason =
        "Command execution was interrupted before completion.";

    private readonly ICommandExecutionRepository _commands;
    private readonly IRconCommandExecutor _rconExecutor;
    private readonly IClock _clock;

    public CommandDispatcher(
        ICommandExecutionRepository commands,
        IRconCommandExecutor rconExecutor,
        IClock clock)
    {
        _commands = commands;
        _rconExecutor = rconExecutor;
        _clock = clock;
    }

    public async Task<int> RecoverInterruptedAsync(
        TimeSpan interruptedAfter,
        CancellationToken cancellationToken)
    {
        if (interruptedAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interruptedAfter),
                "Interrupted command timeout must be positive.");
        }

        var completedAtUtc = _clock.UtcNow;
        var recovered = await _commands.FailInterruptedAsync(
            completedAtUtc - interruptedAfter,
            completedAtUtc,
            InterruptedFailureReason,
            cancellationToken);

        GoldSrcOpsMetrics.RecordCommandsRecovered(recovered);
        return recovered;
    }

    public async Task<CommandDispatchAttemptResult> DispatchNextAsync(
        CancellationToken cancellationToken)
    {
        var context = await _commands.ClaimNextPendingAsync(_clock.UtcNow, cancellationToken);
        if (context is null)
        {
            return CommandDispatchAttemptResult.NoCommand();
        }

        var command = context.Command;
        if (command.Status != CommandExecutionStatus.Running || command.StartedAtUtc is null)
        {
            throw new InvalidOperationException("A claimed command must be running and have a start time.");
        }

        var metricResult = CommandDispatchMetricResult.Failed;
        if (context.RconPort is null)
        {
            command.MarkFailed(_clock.UtcNow, "RCON port is not configured.");
        }
        else if (string.IsNullOrWhiteSpace(context.CredentialSecretReference))
        {
            command.MarkFailed(_clock.UtcNow, "RCON credential is not configured.");
        }
        else
        {
            GoldSrcOpsMetrics.RecordCommandDispatched(command.Type);
            metricResult = await ExecuteAsync(context, cancellationToken);
        }

        var completed = await _commands.CompleteClaimedAsync(
            command,
            command.StartedAtUtc.Value,
            cancellationToken);
        if (!completed)
        {
            return CommandDispatchAttemptResult.CompletionLost(command);
        }

        GoldSrcOpsMetrics.RecordCommandCompleted(command.Type, metricResult);
        return CommandDispatchAttemptResult.Completed(command);
    }

    private async Task<CommandDispatchMetricResult> ExecuteAsync(
        CommandExecutionDispatchContext context,
        CancellationToken cancellationToken)
    {
        var command = context.Command;
        var credentialSecretReference = context.CredentialSecretReference!;

        try
        {
            var dispatchResult = await _rconExecutor.ExecuteAsync(
                new RconCommandExecutionRequest(
                    command.Id,
                    command.ServerId,
                    context.Host,
                    context.RconPort!.Value,
                    credentialSecretReference,
                    command.Type,
                    BuildCommandText(command)),
                cancellationToken);

            return ApplyDispatchResult(command, dispatchResult, credentialSecretReference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            command.MarkFailed(_clock.UtcNow, "RCON command timed out.");
            return CommandDispatchMetricResult.TimedOut;
        }
        catch
        {
            command.MarkFailed(_clock.UtcNow, "RCON command failed.");
            return CommandDispatchMetricResult.Failed;
        }
    }

    private CommandDispatchMetricResult ApplyDispatchResult(
        CommandExecution command,
        RconCommandExecutionResult result,
        string credentialSecretReference)
    {
        switch (result.Kind)
        {
            case RconCommandExecutionResultKind.Succeeded:
                command.MarkSucceeded(
                    _clock.UtcNow,
                    SanitizeExecutorText(
                        result.ResultSummary,
                        "RCON command completed.",
                        credentialSecretReference));
                return CommandDispatchMetricResult.Succeeded;
            case RconCommandExecutionResultKind.Failed:
                command.MarkFailed(
                    _clock.UtcNow,
                    SanitizeExecutorText(
                        result.FailureReason,
                        "RCON command failed.",
                        credentialSecretReference));
                return CommandDispatchMetricResult.Failed;
            case RconCommandExecutionResultKind.TimedOut:
                command.MarkFailed(
                    _clock.UtcNow,
                    SanitizeExecutorText(
                        result.FailureReason,
                        "RCON command timed out.",
                        credentialSecretReference));
                return CommandDispatchMetricResult.TimedOut;
            case RconCommandExecutionResultKind.AuthenticationFailed:
                command.MarkFailed(
                    _clock.UtcNow,
                    SanitizeExecutorText(
                        result.FailureReason,
                        "RCON authentication failed.",
                        credentialSecretReference));
                return CommandDispatchMetricResult.AuthenticationFailed;
            default:
                throw new InvalidOperationException($"Unsupported RCON command result '{result.Kind}'.");
        }
    }

    private static string BuildCommandText(CommandExecution command)
    {
        return command.Type switch
        {
            ServerCommandType.ChangeMap => $"changelevel {command.Payload}",
            ServerCommandType.Restart => "_restart",
            ServerCommandType.Say => $"say {command.Payload}",
            ServerCommandType.Raw => command.Payload!,
            _ => throw new InvalidOperationException($"Unsupported command type '{command.Type}'.")
        };
    }

    private static string SanitizeExecutorText(
        string? value,
        string fallback,
        string credentialSecretReference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().Replace(
            credentialSecretReference,
            "[credential]",
            StringComparison.OrdinalIgnoreCase);
    }
}
