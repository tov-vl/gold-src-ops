using System.Data;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfCommandExecutionRepository : ICommandExecutionRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private const string ClaimNextPostgreSql = """
        WITH candidate AS MATERIALIZED
        (
            SELECT pending_command."Id"
            FROM "goldsrcops"."servers" AS registered_server
            CROSS JOIN LATERAL
            (
                SELECT command_execution."Id", command_execution."RequestedAtUtc"
                FROM "goldsrcops"."command_executions" AS command_execution
                WHERE command_execution."ServerId" = registered_server."Id"
                  AND command_execution."Status" = 'Pending'
                ORDER BY command_execution."RequestedAtUtc", command_execution."Id"
                LIMIT 1
            ) AS pending_command
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM "goldsrcops"."command_executions" AS running_command
                WHERE running_command."ServerId" = registered_server."Id"
                  AND running_command."Status" = 'Running'
            )
            ORDER BY pending_command."RequestedAtUtc", pending_command."Id"
            FOR UPDATE OF registered_server SKIP LOCKED
            LIMIT 1
        )
        UPDATE "goldsrcops"."command_executions" AS claimed_command
        SET "Status" = 'Running',
            "StartedAtUtc" = @startedAtUtc
        FROM candidate
        WHERE claimed_command."Id" = candidate."Id"
          AND claimed_command."Status" = 'Pending'
        RETURNING claimed_command."Id";
        """;

    private readonly GoldSrcOpsDbContext _dbContext;

    public EfCommandExecutionRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        return await _dbContext.Servers
            .AsNoTracking()
            .AnyAsync(x => x.Id == serverId, cancellationToken);
    }

    public async Task<bool> HasCredentialAsync(
        Guid serverId,
        ServerCredentialKind kind,
        CancellationToken cancellationToken)
    {
        return await _dbContext.ServerCredentials
            .AsNoTracking()
            .AnyAsync(x => x.ServerId == serverId && x.Kind == kind, cancellationToken);
    }

    public async Task AddAsync(CommandExecution command, CancellationToken cancellationToken)
    {
        await _dbContext.CommandExecutions.AddAsync(command, cancellationToken);
    }

    public async Task<CommandExecution?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.CommandExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<CommandExecutionDispatchContext?> ClaimNextPendingAsync(
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                _dbContext.Database.ProviderName,
                NpgsqlProviderName,
                StringComparison.Ordinal))
        {
            if (_dbContext.Database.IsRelational())
            {
                throw new NotSupportedException(
                    $"Atomic command claiming is not implemented for provider '{_dbContext.Database.ProviderName}'.");
            }

            return await ClaimNextNonRelationalAsync(startedAtUtc, cancellationToken);
        }

        var commandId = await ClaimNextPostgreSqlAsync(startedAtUtc, cancellationToken);
        if (commandId is null)
        {
            return null;
        }

        var command = await _dbContext.CommandExecutions
            .AsNoTracking()
            .Include(x => x.Server)
            .SingleAsync(x => x.Id == commandId.Value, cancellationToken);

        return await CreateDispatchContextAsync(command, cancellationToken);
    }

    public async Task<bool> CompleteClaimedAsync(
        CommandExecution command,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Status is not CommandExecutionStatus.Succeeded and not CommandExecutionStatus.Failed ||
            command.CompletedAtUtc is null)
        {
            throw new InvalidOperationException("A claimed command must be completed before it can be persisted.");
        }

        if (!_dbContext.Database.IsRelational())
        {
            return await CompleteClaimedNonRelationalAsync(command, claimedAtUtc, cancellationToken);
        }

        var status = command.Status;
        var completedAtUtc = command.CompletedAtUtc;
        var resultSummary = command.ResultSummary;
        var failureReason = command.FailureReason;

        var updated = await _dbContext.CommandExecutions
            .Where(x =>
                x.Id == command.Id &&
                x.Status == CommandExecutionStatus.Running &&
                x.StartedAtUtc == claimedAtUtc)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, status)
                    .SetProperty(x => x.CompletedAtUtc, completedAtUtc)
                    .SetProperty(x => x.ResultSummary, resultSummary)
                    .SetProperty(x => x.FailureReason, failureReason),
                cancellationToken);

        return updated == 1;
    }

    public async Task<int> FailInterruptedAsync(
        DateTimeOffset startedBeforeUtc,
        DateTimeOffset completedAtUtc,
        string failureReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        if (!_dbContext.Database.IsRelational())
        {
            return await FailInterruptedNonRelationalAsync(
                startedBeforeUtc,
                completedAtUtc,
                failureReason,
                cancellationToken);
        }

        return await _dbContext.CommandExecutions
            .Where(x =>
                x.Status == CommandExecutionStatus.Running &&
                (x.StartedAtUtc == null || x.StartedAtUtc <= startedBeforeUtc))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(x => x.Status, CommandExecutionStatus.Failed)
                    .SetProperty(x => x.CompletedAtUtc, completedAtUtc)
                    .SetProperty(x => x.ResultSummary, (string?)null)
                    .SetProperty(x => x.FailureReason, failureReason),
                cancellationToken);
    }

    public async Task<IReadOnlyList<CommandExecution>> ListByServerAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken)
    {
        return await _dbContext.CommandExecutions
            .AsNoTracking()
            .Where(x => x.ServerId == serverId)
            .OrderByDescending(x => x.RequestedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Guid?> ClaimNextPostgreSqlAsync(
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;

        if (closeConnection)
        {
            await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimNextPostgreSql;

            var startedAtParameter = command.CreateParameter();
            startedAtParameter.ParameterName = "startedAtUtc";
            startedAtParameter.DbType = DbType.DateTimeOffset;
            startedAtParameter.Value = startedAtUtc.ToUniversalTime();
            command.Parameters.Add(startedAtParameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is Guid commandId ? commandId : null;
        }
        finally
        {
            if (closeConnection)
            {
                await _dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<CommandExecutionDispatchContext?> ClaimNextNonRelationalAsync(
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var command = await _dbContext.CommandExecutions
            .Include(x => x.Server)
            .Where(x =>
                x.Status == CommandExecutionStatus.Pending &&
                !_dbContext.CommandExecutions.Any(running =>
                    running.ServerId == x.ServerId &&
                    running.Status == CommandExecutionStatus.Running))
            .OrderBy(x => x.RequestedAtUtc)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (command is null)
        {
            return null;
        }

        command.MarkRunning(startedAtUtc);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.Entry(command).State = EntityState.Detached;

        return await CreateDispatchContextAsync(command, cancellationToken);
    }

    private async Task<bool> CompleteClaimedNonRelationalAsync(
        CommandExecution command,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken)
    {
        var current = await _dbContext.CommandExecutions
            .FirstOrDefaultAsync(x => x.Id == command.Id, cancellationToken);
        if (current is null ||
            current.Status != CommandExecutionStatus.Running ||
            current.StartedAtUtc != claimedAtUtc)
        {
            return false;
        }

        if (command.Status == CommandExecutionStatus.Succeeded)
        {
            current.MarkSucceeded(command.CompletedAtUtc!.Value, command.ResultSummary);
        }
        else
        {
            current.MarkFailed(command.CompletedAtUtc!.Value, command.FailureReason!);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<int> FailInterruptedNonRelationalAsync(
        DateTimeOffset startedBeforeUtc,
        DateTimeOffset completedAtUtc,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var interrupted = await _dbContext.CommandExecutions
            .Where(x =>
                x.Status == CommandExecutionStatus.Running &&
                (x.StartedAtUtc == null || x.StartedAtUtc <= startedBeforeUtc))
            .ToListAsync(cancellationToken);

        foreach (var command in interrupted)
        {
            command.MarkFailed(completedAtUtc, failureReason);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return interrupted.Count;
    }

    private async Task<CommandExecutionDispatchContext> CreateDispatchContextAsync(
        CommandExecution command,
        CancellationToken cancellationToken)
    {
        var credentialSecretReference = await _dbContext.ServerCredentials
            .AsNoTracking()
            .Where(x => x.ServerId == command.ServerId && x.Kind == ServerCredentialKind.RconPassword)
            .Select(x => x.SecretReference)
            .FirstOrDefaultAsync(cancellationToken);

        return new CommandExecutionDispatchContext(
            command,
            command.Server.Endpoint.Host,
            command.Server.Endpoint.RconPort,
            credentialSecretReference);
    }
}
