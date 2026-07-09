using GoldSrcOps.Application.Common;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Commands;

public sealed class CommandExecutionService
{
    public const int DefaultCommandHistoryLimit = 50;
    public const int MaxCommandHistoryLimit = 100;

    private readonly ICommandExecutionRepository _commands;
    private readonly IRconCommandExecutor _rconExecutor;
    private readonly IClock _clock;

    public CommandExecutionService(
        ICommandExecutionRepository commands,
        IRconCommandExecutor rconExecutor,
        IClock clock)
    {
        _commands = commands;
        _rconExecutor = rconExecutor;
        _clock = clock;
    }

    public async Task<CommandExecutionCreateResult> QueueAsync(
        Guid serverId,
        CreateCommandExecutionCommand command,
        CancellationToken cancellationToken)
    {
        if (!await _commands.ServerExistsAsync(serverId, cancellationToken))
        {
            return CommandExecutionCreateResult.ServerNotFound();
        }

        if (!await _commands.HasCredentialAsync(serverId, ServerCredentialKind.RconPassword, cancellationToken))
        {
            return CommandExecutionCreateResult.MissingRconCredential();
        }

        var execution = new CommandExecution(
            serverId,
            command.Type,
            command.Payload,
            command.RequestedBy,
            _clock.UtcNow);

        await _commands.AddAsync(execution, cancellationToken);
        await _commands.SaveChangesAsync(cancellationToken);

        return CommandExecutionCreateResult.Created(Map(execution));
    }

    public async Task<CommandExecutionDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var command = await _commands.GetAsync(id, cancellationToken);
        return command is null ? null : Map(command);
    }

    public async Task<CommandExecutionDispatchResult> DispatchAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        var context = await _commands.GetDispatchContextAsync(commandId, cancellationToken);
        if (context is null)
        {
            return CommandExecutionDispatchResult.CommandNotFound();
        }

        var command = context.Command;
        if (command.Status != CommandExecutionStatus.Pending)
        {
            return CommandExecutionDispatchResult.NotPending(Map(command));
        }

        if (context.RconPort is null)
        {
            command.MarkFailed(_clock.UtcNow, "RCON port is not configured.");
            await _commands.SaveChangesAsync(cancellationToken);

            return CommandExecutionDispatchResult.Dispatched(Map(command));
        }

        if (string.IsNullOrWhiteSpace(context.CredentialSecretReference))
        {
            command.MarkFailed(_clock.UtcNow, "RCON credential is not configured.");
            await _commands.SaveChangesAsync(cancellationToken);

            return CommandExecutionDispatchResult.Dispatched(Map(command));
        }

        command.MarkRunning(_clock.UtcNow);
        await _commands.SaveChangesAsync(cancellationToken);

        try
        {
            var dispatchResult = await _rconExecutor.ExecuteAsync(
                new RconCommandExecutionRequest(
                    command.Id,
                    command.ServerId,
                    context.Host,
                    context.RconPort.Value,
                    context.CredentialSecretReference,
                    command.Type,
                    BuildCommandText(command)),
                cancellationToken);

            ApplyDispatchResult(command, dispatchResult, context.CredentialSecretReference);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            command.MarkFailed(_clock.UtcNow, "RCON command timed out.");
        }
        catch
        {
            command.MarkFailed(_clock.UtcNow, "RCON command failed.");
        }

        await _commands.SaveChangesAsync(cancellationToken);

        return CommandExecutionDispatchResult.Dispatched(Map(command));
    }

    public async Task<IReadOnlyList<CommandExecutionDto>?> ListByServerAsync(
        Guid serverId,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!await _commands.ServerExistsAsync(serverId, cancellationToken))
        {
            return null;
        }

        var normalizedLimit = Math.Clamp(limit ?? DefaultCommandHistoryLimit, 1, MaxCommandHistoryLimit);
        var commands = await _commands.ListByServerAsync(serverId, normalizedLimit, cancellationToken);

        return commands.Select(Map).ToArray();
    }

    private static CommandExecutionDto Map(CommandExecution command) =>
        new(
            command.Id,
            command.ServerId,
            command.Type,
            command.Status,
            command.Payload,
            command.RequestedBy,
            command.RequestedAtUtc,
            command.StartedAtUtc,
            command.CompletedAtUtc,
            command.ResultSummary,
            command.FailureReason);

    private void ApplyDispatchResult(
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
                break;
            case RconCommandExecutionResultKind.Failed:
                command.MarkFailed(
                    _clock.UtcNow,
                    SanitizeExecutorText(
                        result.FailureReason,
                        "RCON command failed.",
                        credentialSecretReference));
                break;
            case RconCommandExecutionResultKind.TimedOut:
                command.MarkFailed(
                    _clock.UtcNow,
                    SanitizeExecutorText(
                        result.FailureReason,
                        "RCON command timed out.",
                        credentialSecretReference));
                break;
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
