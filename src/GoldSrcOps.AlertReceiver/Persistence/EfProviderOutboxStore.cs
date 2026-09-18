using System.Data;
using System.Text.Json;
using GoldSrcOps.AlertReceiver.ProviderDelivery;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.AlertReceiver.Persistence;

internal sealed class EfProviderOutboxStore(AlertReceiverDbContext dbContext)
    : IProviderOutboxStore
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string ClaimNextSql = """
        WITH candidate AS MATERIALIZED
        (
            SELECT pending."Id"
            FROM "receiver"."provider_outbox_messages" AS pending
            WHERE pending."Status" = 'Pending'
              AND pending."NextAttemptAtUtc" <= @claimedAtUtc
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM "receiver"."provider_outbox_messages" AS older
                  WHERE older."IncidentId" = pending."IncidentId"
                    AND older."Status" IN ('Pending', 'Processing', 'DeadLetter')
                    AND (older."CreatedAtUtc", older."Id") < (pending."CreatedAtUtc", pending."Id")
              )
            ORDER BY pending."NextAttemptAtUtc", pending."CreatedAtUtc", pending."Id"
            FOR UPDATE OF pending SKIP LOCKED
            LIMIT 1
        )
        UPDATE "receiver"."provider_outbox_messages" AS claimed
        SET "Status" = 'Processing',
            "ClaimId" = @claimId,
            "ClaimedAtUtc" = @claimedAtUtc,
            "AttemptCount" = claimed."AttemptCount" + 1
        FROM candidate
        WHERE claimed."Id" = candidate."Id"
          AND claimed."Status" = 'Pending'
          AND claimed."NextAttemptAtUtc" <= @claimedAtUtc
        RETURNING claimed."Id";
        """;

    public async Task<ClaimedProviderOutboxMessage?> ClaimNextAsync(
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsurePostgreSqlProvider();
        var claimId = Guid.NewGuid();
        var normalizedClaimedAtUtc = claimedAtUtc.ToUniversalTime();
        var messageId = await ClaimNextPostgreSqlAsync(
            claimId,
            normalizedClaimedAtUtc,
            cancellationToken);
        if (messageId is null)
        {
            return null;
        }

        var message = await dbContext.ProviderOutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == messageId && candidate.ClaimId == claimId,
                cancellationToken);
        if (message is null)
        {
            return null;
        }

        using var payload = JsonDocument.Parse(message.Payload);

        return new ClaimedProviderOutboxMessage(
            message.Id,
            message.SourceEventId,
            message.IncidentId,
            message.Action.ToString(),
            message.CreatedAtUtc,
            payload.RootElement.Clone(),
            message.AttemptCount,
            claimId,
            normalizedClaimedAtUtc);
    }

    public async Task<bool> MarkProcessedAsync(
        Guid messageId,
        Guid claimId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateIdentifiers(messageId, claimId);
        var updated = await ClaimedMessage(messageId, claimId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, ProviderOutboxStatus.Processed)
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.ProcessedAtUtc, processedAtUtc.ToUniversalTime())
                    .SetProperty(message => message.LastError, (string?)null),
                cancellationToken);
        return updated == 1;
    }

    public async Task<bool> ScheduleRetryAsync(
        Guid messageId,
        Guid claimId,
        DateTimeOffset nextAttemptAtUtc,
        string error,
        CancellationToken cancellationToken)
    {
        ValidateIdentifiers(messageId, claimId);
        var updated = await ClaimedMessage(messageId, claimId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, ProviderOutboxStatus.Pending)
                    .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc.ToUniversalTime())
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LastError, NormalizeError(error)),
                cancellationToken);
        return updated == 1;
    }

    public async Task<bool> MarkDeadLetterAsync(
        Guid messageId,
        Guid claimId,
        DateTimeOffset deadLetteredAtUtc,
        string error,
        CancellationToken cancellationToken)
    {
        ValidateIdentifiers(messageId, claimId);
        var updated = await ClaimedMessage(messageId, claimId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, ProviderOutboxStatus.DeadLetter)
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.DeadLetteredAtUtc, deadLetteredAtUtc.ToUniversalTime())
                    .SetProperty(message => message.LastError, NormalizeError(error)),
                cancellationToken);
        return updated == 1;
    }

    public async Task<ProviderClaimRecoveryResult> RecoverExpiredClaimsAsync(
        DateTimeOffset expiredBeforeUtc,
        DateTimeOffset recoveredAtUtc,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        var expiredBefore = expiredBeforeUtc.ToUniversalTime();
        var recoveredAt = recoveredAtUtc.ToUniversalTime();

        var deadLettered = await dbContext.ProviderOutboxMessages
            .Where(message =>
                message.Status == ProviderOutboxStatus.Processing &&
                message.ClaimedAtUtc <= expiredBefore &&
                message.AttemptCount >= maxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, ProviderOutboxStatus.DeadLetter)
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.DeadLetteredAtUtc, recoveredAt)
                    .SetProperty(message => message.LastError, "Delivery claim expired after the maximum number of attempts."),
                cancellationToken);
        var retried = await dbContext.ProviderOutboxMessages
            .Where(message =>
                message.Status == ProviderOutboxStatus.Processing &&
                message.ClaimedAtUtc <= expiredBefore &&
                message.AttemptCount < maxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, ProviderOutboxStatus.Pending)
                    .SetProperty(message => message.NextAttemptAtUtc, recoveredAt)
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LastError, "Delivery claim expired before completion."),
                cancellationToken);

        return new ProviderClaimRecoveryResult(retried, deadLettered);
    }

    public async Task<int> DeleteProcessedBatchAsync(
        DateTimeOffset cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var ids = dbContext.ProviderOutboxMessages
            .Where(message =>
                message.Status == ProviderOutboxStatus.Processed &&
                message.ProcessedAtUtc < cutoffUtc.ToUniversalTime())
            .OrderBy(message => message.ProcessedAtUtc)
            .ThenBy(message => message.Id)
            .Take(batchSize)
            .Select(message => message.Id);
        return await dbContext.ProviderOutboxMessages
            .Where(message => ids.Contains(message.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<ProviderOutboxStatistics> GetStatisticsAsync(
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.ProviderOutboxMessages
            .Where(message => message.Status == ProviderOutboxStatus.Pending)
            .GroupBy(static _ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Oldest = group.Min(message => message.CreatedAtUtc),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var processing = await dbContext.ProviderOutboxMessages.LongCountAsync(
            message => message.Status == ProviderOutboxStatus.Processing,
            cancellationToken);
        var deadLetters = await dbContext.ProviderOutboxMessages.LongCountAsync(
            message => message.Status == ProviderOutboxStatus.DeadLetter,
            cancellationToken);
        return new ProviderOutboxStatistics(
            pending?.Count ?? 0,
            processing,
            deadLetters,
            pending?.Oldest);
    }

    private IQueryable<ProviderOutboxMessage> ClaimedMessage(Guid messageId, Guid claimId) =>
        dbContext.ProviderOutboxMessages.Where(message =>
            message.Id == messageId &&
            message.Status == ProviderOutboxStatus.Processing &&
            message.ClaimId == claimId);

    private async Task<Guid?> ClaimNextPostgreSqlAsync(
        Guid claimId,
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimNextSql;
            AddParameter(command, "claimId", DbType.Guid, claimId);
            AddParameter(command, "claimedAtUtc", DbType.DateTimeOffset, claimedAtUtc);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is Guid id ? id : null;
        }
        finally
        {
            if (closeConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static void AddParameter(
        System.Data.Common.DbCommand command,
        string name,
        DbType type,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var normalized = error.Trim();
        return normalized.Length <= ProviderOutboxMessage.MaxErrorLength
            ? normalized
            : normalized[..ProviderOutboxMessage.MaxErrorLength];
    }

    private static void ValidateIdentifiers(Guid messageId, Guid claimId)
    {
        if (messageId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException(
                "Message and claim identifiers must not be empty.",
                nameof(messageId));
        }
    }

    private void EnsurePostgreSqlProvider()
    {
        if (!string.Equals(dbContext.Database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Provider outbox claiming requires PostgreSQL.");
        }
    }
}
