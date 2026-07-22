using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Commands;

public sealed class CommandExecutionService
{
    public const int DefaultCommandHistoryLimit = 50;
    public const int MaxCommandHistoryLimit = 100;

    private readonly ICommandExecutionRepository _commands;
    private readonly IClock _clock;

    public CommandExecutionService(
        ICommandExecutionRepository commands,
        IClock clock)
    {
        _commands = commands;
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

        GoldSrcOpsMetrics.RecordCommandQueued(execution.Type);

        return CommandExecutionCreateResult.Created(Map(execution));
    }

    public async Task<CommandExecutionDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var command = await _commands.GetAsync(id, cancellationToken);
        return command is null ? null : Map(command);
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
}
