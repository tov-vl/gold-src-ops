using GoldSrcOps.Application.Alerts;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class EfAlertDeliveryReadRepository : IAlertDeliveryReadRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfAlertDeliveryReadRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<DeadLetterListItemDto>> ListDeadLettersAsync(
        DeadLetterPagePosition? position,
        int maxCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        var result = new List<DeadLetterListItemDto>(maxCount);
        if (position is null || position.DeadLetteredAtUtc is not null)
        {
            var knownTimestampQuery = _dbContext.OutboxMessages
                .AsNoTracking()
                .Where(message =>
                    message.Status == OutboxMessageStatus.DeadLetter &&
                    message.DeadLetteredAtUtc != null);

            if (position is not null)
            {
                var knownPosition = position;
                knownTimestampQuery = knownTimestampQuery.Where(message =>
                    EF.Functions.LessThan(
                        ValueTuple.Create(
                            message.DeadLetteredAtUtc!.Value,
                            message.OccurredAtUtc,
                            message.Id),
                        ValueTuple.Create(
                            knownPosition.DeadLetteredAtUtc!.Value,
                            knownPosition.OccurredAtUtc,
                            knownPosition.EventId)));
            }

            var knownTimestampItems = await Project(knownTimestampQuery
                    .OrderByDescending(message => message.DeadLetteredAtUtc)
                    .ThenByDescending(message => message.OccurredAtUtc)
                    .ThenByDescending(message => message.Id)
                    .Take(maxCount))
                .ToListAsync(cancellationToken);
            result.AddRange(knownTimestampItems);
        }

        var remaining = maxCount - result.Count;
        if (remaining > 0)
        {
            var legacyQuery = _dbContext.OutboxMessages
                .AsNoTracking()
                .Where(message =>
                    message.Status == OutboxMessageStatus.DeadLetter &&
                    message.DeadLetteredAtUtc == null);

            if (position?.DeadLetteredAtUtc is null && position is not null)
            {
                var legacyPosition = position;
                legacyQuery = legacyQuery.Where(message =>
                    EF.Functions.LessThan(
                        ValueTuple.Create(message.OccurredAtUtc, message.Id),
                        ValueTuple.Create(legacyPosition.OccurredAtUtc, legacyPosition.EventId)));
            }

            var legacyItems = await Project(legacyQuery
                    .OrderByDescending(message => message.OccurredAtUtc)
                    .ThenByDescending(message => message.Id)
                    .Take(remaining))
                .ToListAsync(cancellationToken);
            result.AddRange(legacyItems);
        }

        return result;
    }

    public async Task<DeadLetterDetailsDto?> GetDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var message = await _dbContext.OutboxMessages
            .AsNoTracking()
            .Where(candidate =>
                candidate.Id == eventId &&
                candidate.Status == OutboxMessageStatus.DeadLetter)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.EventType,
                candidate.PayloadVersion,
                candidate.AggregateType,
                candidate.AggregateId,
                candidate.OccurredAtUtc,
                candidate.Payload,
                candidate.AttemptCount,
                candidate.ReplayCount,
                candidate.DeadLetteredAtUtc,
                candidate.LastError
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (message is null)
        {
            return null;
        }

        var newerMessage = await _dbContext.OutboxMessages
            .AsNoTracking()
            .Where(candidate =>
                candidate.AggregateType == message.AggregateType &&
                candidate.AggregateId == message.AggregateId &&
                EF.Functions.GreaterThan(
                    ValueTuple.Create(candidate.OccurredAtUtc, candidate.Id),
                    ValueTuple.Create(message.OccurredAtUtc, message.Id)))
            .OrderByDescending(candidate => candidate.OccurredAtUtc)
            .ThenByDescending(candidate => candidate.Id)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Status,
                candidate.OccurredAtUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DeadLetterDetailsDto(
            message.Id,
            message.EventType,
            message.PayloadVersion,
            message.AggregateType,
            message.AggregateId,
            message.OccurredAtUtc,
            message.Payload,
            message.AttemptCount,
            message.ReplayCount,
            message.DeadLetteredAtUtc,
            message.LastError,
            newerMessage is null
                ? null
                : new NewerOutboxMessageDto(
                    newerMessage.Id,
                    newerMessage.Status.ToString(),
                    newerMessage.OccurredAtUtc));
    }

    private static IQueryable<DeadLetterListItemDto> Project(IQueryable<OutboxMessage> query)
    {
        return query.Select(message => new DeadLetterListItemDto(
            message.Id,
            message.EventType,
            message.PayloadVersion,
            message.AggregateType,
            message.AggregateId,
            message.OccurredAtUtc,
            message.AttemptCount,
            message.ReplayCount,
            message.DeadLetteredAtUtc,
            message.LastError));
    }
}
