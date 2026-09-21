using System.Data;
using GoldSrcOps.AlertReceiver.Persistence;
using GoldSrcOps.Contracts.ProviderDelivery;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.AlertReceiver.ProviderOperations;

internal sealed class ProviderDeliveryOperationsService(
    AlertReceiverDbContext dbContext,
    TimeProvider timeProvider)
{
    private static readonly DateTimeOffset PostgresEpoch =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public const int DefaultDeadLetterLimit = 50;
    public const int MaxDeadLetterLimit = 100;

    public async Task<ProviderDeadLetterListPage> ListDeadLettersAsync(
        ProviderDeadLetterPagePosition? position,
        int? limit,
        CancellationToken cancellationToken)
    {
        var effectiveLimit = limit ?? DefaultDeadLetterLimit;
        if (effectiveLimit is < 1 or > MaxDeadLetterLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var query = DeadLetters();
        if (position is not null)
        {
            query = query.Where(message =>
                EF.Functions.LessThan(
                    ValueTuple.Create(message.DeadLetteredAtUtc!.Value, message.Id),
                    ValueTuple.Create(position.DeadLetteredAtUtc, position.MessageId)));
        }

        var rows = await Project(query
                .OrderByDescending(message => message.DeadLetteredAtUtc)
                .ThenByDescending(message => message.Id)
                .Take(effectiveLimit + 1))
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > effectiveLimit;
        IReadOnlyList<ProviderDeadLetterListItemResponse> items = hasMore
            ? rows.Take(effectiveLimit).ToArray()
            : rows;
        var nextPosition = hasMore
            ? new ProviderDeadLetterPagePosition(
                items[^1].DeadLetteredAtUtc,
                items[^1].MessageId)
            : null;

        return new ProviderDeadLetterListPage(effectiveLimit, items, nextPosition);
    }

    public Task<ProviderDeadLetterListItemResponse?> GetDeadLetterAsync(
        Guid messageId,
        CancellationToken cancellationToken) =>
        Project(DeadLetters().Where(message => message.Id == messageId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ProviderDeadLetterReviewResponse?> GetReviewAsync(
        Guid requestId,
        CancellationToken cancellationToken) =>
        dbContext.ProviderOutboxReviews
            .AsNoTracking()
            .Where(review => review.RequestId == requestId)
            .Select(review => new ProviderDeadLetterReviewResponse(
                review.RequestId,
                review.MessageId,
                review.RequestedBy,
                review.Reason,
                review.ReviewedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProviderDeadLetterReviewResult> ReviewDeadLetterAsync(
        ProviderDeadLetterReviewCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await AcquireLockAsync($"provider-review-request:{command.RequestId:D}", cancellationToken);
        var existingRequest = await dbContext.ProviderOutboxReviews
            .AsNoTracking()
            .SingleOrDefaultAsync(
                review => review.RequestId == command.RequestId,
                cancellationToken);
        if (existingRequest is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existingRequest.MessageId == command.MessageId &&
                string.Equals(existingRequest.RequestedBy, command.RequestedBy, StringComparison.Ordinal) &&
                string.Equals(existingRequest.Reason, command.Reason, StringComparison.Ordinal)
                ? ProviderDeadLetterReviewResult.Idempotent(Map(existingRequest))
                : ProviderDeadLetterReviewResult.Conflict();
        }

        await AcquireLockAsync($"provider-review-message:{command.MessageId:D}", cancellationToken);
        var existingReview = await dbContext.ProviderOutboxReviews
            .AsNoTracking()
            .SingleOrDefaultAsync(
                review => review.MessageId == command.MessageId,
                cancellationToken);
        if (existingReview is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProviderDeadLetterReviewResult.AlreadyReviewed(Map(existingReview));
        }

        var messageStatus = await dbContext.ProviderOutboxMessages
            .AsNoTracking()
            .Where(message => message.Id == command.MessageId)
            .Select(message => (ProviderOutboxStatus?)message.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (messageStatus is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProviderDeadLetterReviewResult.NotFound();
        }

        if (messageStatus != ProviderOutboxStatus.DeadLetter)
        {
            await transaction.CommitAsync(cancellationToken);
            return ProviderDeadLetterReviewResult.NotDeadLetter();
        }

        var review = ProviderOutboxReview.Create(
            command.RequestId,
            command.MessageId,
            command.RequestedBy,
            command.Reason,
            ToPostgresPrecision(timeProvider.GetUtcNow()));
        dbContext.ProviderOutboxReviews.Add(review);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProviderDeadLetterReviewResult.Accepted(Map(review));
    }

    private IQueryable<ProviderOutboxMessage> DeadLetters() =>
        dbContext.ProviderOutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.Status == ProviderOutboxStatus.DeadLetter &&
                message.DeadLetteredAtUtc != null);

    private IQueryable<ProviderDeadLetterListItemResponse> Project(
        IQueryable<ProviderOutboxMessage> query) =>
        from message in query
        join review in dbContext.ProviderOutboxReviews.AsNoTracking()
            on message.Id equals review.MessageId into reviews
        from review in reviews.DefaultIfEmpty()
        select new ProviderDeadLetterListItemResponse(
            message.Id,
            message.SourceEventId,
            message.IncidentId,
            message.Action.ToString(),
            message.CreatedAtUtc,
            message.AttemptCount,
            message.DeadLetteredAtUtc!.Value,
            message.LastError,
            review == null
                ? null
                : new ProviderDeadLetterReviewResponse(
                    review.RequestId,
                    review.MessageId,
                    review.RequestedBy,
                    review.Reason,
                    review.ReviewedAtUtc));

    private Task<int> AcquireLockAsync(string key, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));",
            cancellationToken);

    private static ProviderDeadLetterReviewResponse Map(ProviderOutboxReview review) =>
        new(
            review.RequestId,
            review.MessageId,
            review.RequestedBy,
            review.Reason,
            review.ReviewedAtUtc);

    private static DateTimeOffset ToPostgresPrecision(DateTimeOffset value)
    {
        var utcValue = value.ToUniversalTime();
        var microseconds =
            (utcValue.UtcTicks - PostgresEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond;
        return PostgresEpoch.AddTicks(microseconds * TimeSpan.TicksPerMicrosecond);
    }
}

internal sealed record ProviderDeadLetterListPage(
    int Limit,
    IReadOnlyList<ProviderDeadLetterListItemResponse> Items,
    ProviderDeadLetterPagePosition? NextPosition);

internal sealed record ProviderDeadLetterReviewCommand(
    Guid RequestId,
    Guid MessageId,
    string RequestedBy,
    string Reason);

internal enum ProviderDeadLetterReviewResultKind
{
    Accepted,
    Idempotent,
    NotFound,
    NotDeadLetter,
    Conflict,
    AlreadyReviewed,
}

internal sealed record ProviderDeadLetterReviewResult(
    ProviderDeadLetterReviewResultKind Kind,
    ProviderDeadLetterReviewResponse? Review)
{
    public static ProviderDeadLetterReviewResult Accepted(ProviderDeadLetterReviewResponse review) =>
        new(ProviderDeadLetterReviewResultKind.Accepted, review);

    public static ProviderDeadLetterReviewResult Idempotent(ProviderDeadLetterReviewResponse review) =>
        new(ProviderDeadLetterReviewResultKind.Idempotent, review);

    public static ProviderDeadLetterReviewResult NotFound() =>
        new(ProviderDeadLetterReviewResultKind.NotFound, null);

    public static ProviderDeadLetterReviewResult NotDeadLetter() =>
        new(ProviderDeadLetterReviewResultKind.NotDeadLetter, null);

    public static ProviderDeadLetterReviewResult Conflict() =>
        new(ProviderDeadLetterReviewResultKind.Conflict, null);

    public static ProviderDeadLetterReviewResult AlreadyReviewed(ProviderDeadLetterReviewResponse review) =>
        new(ProviderDeadLetterReviewResultKind.AlreadyReviewed, review);
}
