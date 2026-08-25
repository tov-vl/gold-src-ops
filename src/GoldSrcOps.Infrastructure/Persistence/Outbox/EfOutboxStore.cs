using System.Data;
using GoldSrcOps.Application.Alerts;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class EfOutboxStore(GoldSrcOpsDbContext dbContext) : IOutboxStore
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string ClaimNextPostgreSql = """
        WITH candidate AS MATERIALIZED
        (
            SELECT pending_message."Id"
            FROM "goldsrcops"."outbox_messages" AS pending_message
            WHERE pending_message."Status" = 'Pending'
              AND pending_message."NextAttemptAtUtc" <= @claimedAtUtc
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM "goldsrcops"."outbox_messages" AS older_message
                  WHERE older_message."AggregateType" = pending_message."AggregateType"
                    AND older_message."AggregateId" = pending_message."AggregateId"
                    AND older_message."Status" IN ('Pending', 'Processing')
                    AND (older_message."OccurredAtUtc", older_message."Id")
                        < (pending_message."OccurredAtUtc", pending_message."Id")
              )
            ORDER BY pending_message."NextAttemptAtUtc",
                     pending_message."OccurredAtUtc",
                     pending_message."Id"
            FOR UPDATE OF pending_message SKIP LOCKED
            LIMIT 1
        )
        UPDATE "goldsrcops"."outbox_messages" AS claimed_message
        SET "Status" = 'Processing',
            "ClaimId" = @claimId,
            "ClaimedAtUtc" = @claimedAtUtc,
            "AttemptCount" = claimed_message."AttemptCount" + 1
        FROM candidate
        WHERE claimed_message."Id" = candidate."Id"
          AND claimed_message."Status" = 'Pending'
          AND claimed_message."NextAttemptAtUtc" <= @claimedAtUtc
        RETURNING claimed_message."Id";
        """;

    public async Task<ClaimedOutboxMessage?> ClaimNextPendingAsync(
        DateTimeOffset claimedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsurePostgreSqlProvider();

        var normalizedClaimedAtUtc = claimedAtUtc.ToUniversalTime();
        var claimId = Guid.NewGuid();
        var messageId = await ClaimNextPostgreSqlAsync(
            claimId,
            normalizedClaimedAtUtc,
            cancellationToken);
        if (messageId is null)
        {
            return null;
        }

        var message = await dbContext.OutboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == messageId.Value &&
                    candidate.Status == OutboxMessageStatus.Processing &&
                    candidate.ClaimId == claimId,
                cancellationToken);

        return message is null
            ? null
            : new ClaimedOutboxMessage(
                message.Id,
                message.EventType,
                message.PayloadVersion,
                message.AggregateType,
                message.AggregateId,
                message.OccurredAtUtc,
                message.Payload,
                message.AttemptCount,
                message.ClaimId ?? throw new InvalidOperationException("Claimed message has no claim id."),
                message.ClaimedAtUtc ?? throw new InvalidOperationException("Claimed message has no claim time."));
    }

    public async Task<bool> MarkProcessedAsync(
        Guid messageId,
        Guid claimId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsurePostgreSqlProvider();
        ValidateIdentifier(messageId, nameof(messageId));
        ValidateIdentifier(claimId, nameof(claimId));

        var updated = await dbContext.OutboxMessages
            .Where(message =>
                message.Id == messageId &&
                message.Status == OutboxMessageStatus.Processing &&
                message.ClaimId == claimId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, OutboxMessageStatus.Processed)
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
        string lastError,
        CancellationToken cancellationToken)
    {
        EnsurePostgreSqlProvider();
        ValidateIdentifier(messageId, nameof(messageId));
        ValidateIdentifier(claimId, nameof(claimId));
        var normalizedError = NormalizeError(lastError);

        var updated = await dbContext.OutboxMessages
            .Where(message =>
                message.Id == messageId &&
                message.Status == OutboxMessageStatus.Processing &&
                message.ClaimId == claimId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, OutboxMessageStatus.Pending)
                    .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc.ToUniversalTime())
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.ProcessedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LastError, normalizedError),
                cancellationToken);

        return updated == 1;
    }

    public async Task<int> RecoverExpiredClaimsAsync(
        DateTimeOffset expiredBeforeUtc,
        DateTimeOffset nextAttemptAtUtc,
        string lastError,
        CancellationToken cancellationToken)
    {
        EnsurePostgreSqlProvider();
        var normalizedError = NormalizeError(lastError);

        return await dbContext.OutboxMessages
            .Where(message =>
                message.Status == OutboxMessageStatus.Processing &&
                message.ClaimedAtUtc <= expiredBeforeUtc.ToUniversalTime())
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.Status, OutboxMessageStatus.Pending)
                    .SetProperty(message => message.NextAttemptAtUtc, nextAttemptAtUtc.ToUniversalTime())
                    .SetProperty(message => message.ClaimId, (Guid?)null)
                    .SetProperty(message => message.ClaimedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.ProcessedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(message => message.LastError, normalizedError),
                cancellationToken);
    }

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
            command.CommandText = ClaimNextPostgreSql;

            var claimIdParameter = command.CreateParameter();
            claimIdParameter.ParameterName = "claimId";
            claimIdParameter.DbType = DbType.Guid;
            claimIdParameter.Value = claimId;
            command.Parameters.Add(claimIdParameter);

            var claimedAtParameter = command.CreateParameter();
            claimedAtParameter.ParameterName = "claimedAtUtc";
            claimedAtParameter.DbType = DbType.DateTimeOffset;
            claimedAtParameter.Value = claimedAtUtc;
            command.Parameters.Add(claimedAtParameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is Guid messageId ? messageId : null;
        }
        finally
        {
            if (closeConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static string NormalizeError(string lastError)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastError);

        var normalized = lastError.Trim();
        if (normalized.Length > OutboxMessage.MaxErrorLength)
        {
            throw new ArgumentException(
                $"Error must not exceed {OutboxMessage.MaxErrorLength} characters.",
                nameof(lastError));
        }

        return normalized;
    }

    private static void ValidateIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }

    private void EnsurePostgreSqlProvider()
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                NpgsqlProviderName,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Outbox claiming is not implemented for provider '{dbContext.Database.ProviderName}'.");
        }
    }
}
