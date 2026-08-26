using System.Data;
using GoldSrcOps.Application.Alerts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class EfAlertDeliveryReplayRepository(GoldSrcOpsDbContext dbContext)
    : IAlertDeliveryReplayRepository
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string ReplayRequestPrimaryKey = "PK_outbox_replay_requests";

    public async Task<DeadLetterReplayResult> ReplayAsync(
        Guid requestId,
        Guid eventId,
        string requestedBy,
        DateTimeOffset requestedAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        EnsurePostgreSqlProvider();

        var existingRequest = await FindReplayAsync(requestId, cancellationToken);
        if (existingRequest is not null)
        {
            return MapExistingRequest(existingRequest, eventId, requestedBy, reason);
        }

        var targetIdentity = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message => message.Id == eventId)
            .Select(message => new ReplayTargetIdentity(
                message.AggregateType,
                message.AggregateId))
            .SingleOrDefaultAsync(cancellationToken);

        if (targetIdentity is null)
        {
            return DeadLetterReplayResult.EventNotFound();
        }

        if (!string.Equals(
                targetIdentity.AggregateType,
                IncidentAlertEvents.AggregateType,
                StringComparison.Ordinal))
        {
            return DeadLetterReplayResult.EventNotReplayable();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            if (!await LockAvailabilityIncidentAsync(
                    targetIdentity.AggregateId,
                    cancellationToken))
            {
                await RollbackAsync(transaction, cancellationToken);
                return DeadLetterReplayResult.EventNotReplayable();
            }

            existingRequest = await FindReplayAsync(requestId, cancellationToken);
            if (existingRequest is not null)
            {
                await RollbackAsync(transaction, cancellationToken);
                return MapExistingRequest(existingRequest, eventId, requestedBy, reason);
            }

            var message = await LockOutboxMessageAsync(eventId, cancellationToken);
            if (message is null)
            {
                await RollbackAsync(transaction, cancellationToken);
                return DeadLetterReplayResult.EventNotFound();
            }

            if (message.Status != OutboxMessageStatus.DeadLetter)
            {
                await RollbackAsync(transaction, cancellationToken);
                return DeadLetterReplayResult.EventNotDeadLetter();
            }

            var newerMessages = await LockNewerActiveMessagesAsync(message, cancellationToken);
            if (newerMessages.Any(candidate =>
                    candidate.Status == OutboxMessageStatus.Processing))
            {
                await RollbackAsync(transaction, cancellationToken);
                return DeadLetterReplayResult.NewerEventProcessing();
            }

            var replayRequest = new OutboxReplayRequest(
                requestId,
                message,
                requestedBy,
                requestedAtUtc,
                reason,
                requestedAtUtc);

            dbContext.OutboxReplayRequests.Add(replayRequest);
            message.Replay(requestedAtUtc);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return DeadLetterReplayResult.Accepted(Map(replayRequest));
        }
        catch (DbUpdateException exception) when (IsReplayRequestKeyViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();

            existingRequest = await FindReplayAsync(requestId, cancellationToken);
            if (existingRequest is null)
            {
                throw;
            }

            return MapExistingRequest(existingRequest, eventId, requestedBy, reason);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<DeadLetterReplayRecordDto?> GetReplayAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await FindReplayAsync(requestId, cancellationToken);
        return request is null ? null : Map(request);
    }

    private Task<OutboxReplayRequest?> FindReplayAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        dbContext.OutboxReplayRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(request => request.Id == requestId, cancellationToken);

    private async Task<bool> LockAvailabilityIncidentAsync(
        Guid incidentId,
        CancellationToken cancellationToken)
    {
        var incidents = await dbContext.AvailabilityIncidents
            .FromSqlInterpolated($$"""
                SELECT *
                FROM "goldsrcops"."availability_incidents"
                WHERE "Id" = {{incidentId}}
                FOR UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return incidents.Count == 1;
    }

    private async Task<OutboxMessage?> LockOutboxMessageAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var messages = await dbContext.OutboxMessages
            .FromSqlInterpolated($$"""
                SELECT *
                FROM "goldsrcops"."outbox_messages"
                WHERE "Id" = {{eventId}}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        return messages.SingleOrDefault();
    }

    private Task<List<OutboxMessage>> LockNewerActiveMessagesAsync(
        OutboxMessage message,
        CancellationToken cancellationToken) =>
        dbContext.OutboxMessages
            .FromSqlInterpolated($$"""
                SELECT *
                FROM "goldsrcops"."outbox_messages"
                WHERE "AggregateType" = {{message.AggregateType}}
                  AND "AggregateId" = {{message.AggregateId}}
                  AND "Status" IN ('Pending', 'Processing')
                  AND ("OccurredAtUtc", "Id") > ({{message.OccurredAtUtc}}, {{message.Id}})
                ORDER BY "OccurredAtUtc", "Id"
                FOR UPDATE
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    private static DeadLetterReplayResult MapExistingRequest(
        OutboxReplayRequest request,
        Guid eventId,
        string requestedBy,
        string reason)
    {
        var sameIntent = request.OutboxMessageId == eventId &&
            string.Equals(request.RequestedBy, requestedBy, StringComparison.Ordinal) &&
            string.Equals(request.Reason, reason, StringComparison.Ordinal);

        return sameIntent
            ? DeadLetterReplayResult.Idempotent(Map(request))
            : DeadLetterReplayResult.IdempotencyConflict();
    }

    private static DeadLetterReplayRecordDto Map(OutboxReplayRequest request) =>
        new(
            request.Id,
            request.OutboxMessageId,
            request.RequestedBy,
            request.RequestedAtUtc,
            request.Reason,
            request.ReplayNumber,
            request.PreviousAttemptCount,
            request.PreviousDeadLetteredAtUtc,
            request.NextAttemptAtUtc);

    private static bool IsReplayRequestKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ReplayRequestPrimaryKey
        };

    private async Task RollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private void EnsurePostgreSqlProvider()
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                NpgsqlProviderName,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "Dead-letter replay requires the PostgreSQL provider.");
        }
    }

    private sealed record ReplayTargetIdentity(string AggregateType, Guid AggregateId);
}
