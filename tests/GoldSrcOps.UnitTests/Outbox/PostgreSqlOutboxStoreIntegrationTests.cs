using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Infrastructure.Persistence.Outbox;
using GoldSrcOps.UnitTests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.UnitTests.Outbox;

public sealed class PostgreSqlOutboxStoreIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_dispatchers_claim_each_due_message_at_most_once()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var now = new DateTimeOffset(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        var first = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-2));
        var second = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-1));
        await SeedAsync(factory, first, second);

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => ClaimAsync(factory, now)));

        var claimed = claims.OfType<ClaimedOutboxMessage>().ToArray();
        claimed.Should().HaveCount(2);
        claimed.Select(message => message.Id).Should().OnlyHaveUniqueItems();
        claimed.Select(message => message.ClaimId).Should().OnlyHaveUniqueItems();
        claimed.Select(message => message.AttemptCount).Should().OnlyContain(count => count == 1);
        claims.Count(message => message is null).Should().Be(2);

        var persisted = await factory.ExecuteDbContextAsync(dbContext =>
            dbContext.OutboxMessages
                .AsNoTracking()
                .OrderBy(message => message.OccurredAtUtc)
                .ToListAsync());
        persisted.Should().OnlyContain(message =>
            message.Status == OutboxMessageStatus.Processing &&
            message.AttemptCount == 1 &&
            message.ClaimId != null &&
            message.ClaimedAtUtc == now);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Older_active_message_blocks_later_message_for_the_same_incident()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var incidentId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 25, 11, 0, 0, TimeSpan.Zero);
        var unavailable = CreateMessage(
            Guid.NewGuid(),
            incidentId,
            now.AddMinutes(-2),
            IncidentAlertEvents.ServerUnavailable);
        var recovered = CreateMessage(
            Guid.NewGuid(),
            incidentId,
            now.AddMinutes(-1),
            IncidentAlertEvents.ServerRecovered);
        await SeedAsync(factory, unavailable, recovered);

        var firstClaim = await ClaimAsync(factory, now);

        firstClaim.Should().NotBeNull();
        firstClaim!.Id.Should().Be(unavailable.Id);
        (await ClaimAsync(factory, now.AddSeconds(1))).Should().BeNull();

        var retryAtUtc = now.AddMinutes(5);
        (await ScheduleRetryAsync(
            factory,
            firstClaim.Id,
            firstClaim.ClaimId,
            retryAtUtc,
            "webhook unavailable")).Should().BeTrue();
        (await ClaimAsync(factory, now.AddMinutes(1))).Should().BeNull();

        var retryClaim = await ClaimAsync(factory, retryAtUtc);
        retryClaim.Should().NotBeNull();
        retryClaim!.Id.Should().Be(unavailable.Id);
        retryClaim.AttemptCount.Should().Be(2);
        (await MarkProcessedAsync(
            factory,
            retryClaim.Id,
            retryClaim.ClaimId,
            retryAtUtc.AddSeconds(1))).Should().BeTrue();

        var nextClaim = await ClaimAsync(factory, retryAtUtc.AddSeconds(2));
        nextClaim.Should().NotBeNull();
        nextClaim!.Id.Should().Be(recovered.Id);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Completion_and_retry_require_the_active_claim_and_honor_retry_schedule()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var message = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-1));
        await SeedAsync(factory, message);
        var claim = await ClaimAsync(factory, now);
        claim.Should().NotBeNull();
        var staleClaimId = Guid.NewGuid();

        (await MarkProcessedAsync(
            factory,
            message.Id,
            staleClaimId,
            now.AddSeconds(1))).Should().BeFalse();
        (await ScheduleRetryAsync(
            factory,
            message.Id,
            staleClaimId,
            now.AddMinutes(1),
            "stale attempt")).Should().BeFalse();

        var retryAtUtc = now.AddMinutes(2);
        (await ScheduleRetryAsync(
            factory,
            message.Id,
            claim!.ClaimId,
            retryAtUtc,
            "  request timed out  ")).Should().BeTrue();
        (await ClaimAsync(factory, retryAtUtc.AddTicks(-1))).Should().BeNull();

        var retryClaim = await ClaimAsync(factory, retryAtUtc);
        retryClaim.Should().NotBeNull();
        retryClaim!.Id.Should().Be(message.Id);
        retryClaim.ClaimId.Should().NotBe(claim.ClaimId);
        retryClaim.AttemptCount.Should().Be(2);
        (await MarkProcessedAsync(
            factory,
            message.Id,
            claim.ClaimId,
            retryAtUtc.AddSeconds(1))).Should().BeFalse();

        var processedAtUtc = retryAtUtc.AddSeconds(2);
        (await MarkProcessedAsync(
            factory,
            message.Id,
            retryClaim.ClaimId,
            processedAtUtc)).Should().BeTrue();

        var persisted = await GetMessageAsync(factory, message.Id);
        persisted.Status.Should().Be(OutboxMessageStatus.Processed);
        persisted.AttemptCount.Should().Be(2);
        persisted.ClaimId.Should().BeNull();
        persisted.ClaimedAtUtc.Should().BeNull();
        persisted.ProcessedAtUtc.Should().Be(processedAtUtc);
        persisted.LastError.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Expired_claim_is_recovered_and_reclaimed_with_a_new_token()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var claimedAtUtc = new DateTimeOffset(2026, 8, 25, 13, 0, 0, TimeSpan.Zero);
        var message = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), claimedAtUtc.AddMinutes(-1));
        await SeedAsync(factory, message);
        var interruptedClaim = await ClaimAsync(factory, claimedAtUtc);
        interruptedClaim.Should().NotBeNull();

        (await RecoverExpiredClaimsAsync(
            factory,
            claimedAtUtc.AddSeconds(-1),
            claimedAtUtc,
            maxAttempts: 3,
            lastError: "dispatcher interrupted")).Should().Be(new OutboxClaimRecoveryResult(
                RetryScheduled: 0,
                DeadLettered: 0));

        var retryAtUtc = claimedAtUtc.AddMinutes(1);
        (await RecoverExpiredClaimsAsync(
            factory,
            claimedAtUtc.AddSeconds(1),
            retryAtUtc,
            maxAttempts: 3,
            lastError: "  dispatcher interrupted  ")).Should().Be(new OutboxClaimRecoveryResult(
                RetryScheduled: 1,
                DeadLettered: 0));
        (await MarkProcessedAsync(
            factory,
            message.Id,
            interruptedClaim!.ClaimId,
            retryAtUtc)).Should().BeFalse();
        (await ClaimAsync(factory, retryAtUtc.AddTicks(-1))).Should().BeNull();

        var recovered = await GetMessageAsync(factory, message.Id);
        recovered.Status.Should().Be(OutboxMessageStatus.Pending);
        recovered.AttemptCount.Should().Be(1);
        recovered.NextAttemptAtUtc.Should().Be(retryAtUtc);
        recovered.ClaimId.Should().BeNull();
        recovered.ClaimedAtUtc.Should().BeNull();
        recovered.LastError.Should().Be("dispatcher interrupted");

        var replacementClaim = await ClaimAsync(factory, retryAtUtc);
        replacementClaim.Should().NotBeNull();
        replacementClaim!.Id.Should().Be(message.Id);
        replacementClaim.ClaimId.Should().NotBe(interruptedClaim.ClaimId);
        replacementClaim.AttemptCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Expired_claim_at_the_attempt_limit_moves_directly_to_dead_letter()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var claimedAtUtc = new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero);
        var message = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), claimedAtUtc.AddMinutes(-1));
        await SeedAsync(factory, message);
        var claim = await ClaimAsync(factory, claimedAtUtc);
        claim.Should().NotBeNull();
        claim!.AttemptCount.Should().Be(1);

        var recovered = await RecoverExpiredClaimsAsync(
            factory,
            claimedAtUtc.AddSeconds(1),
            claimedAtUtc.AddMinutes(1),
            maxAttempts: 1,
            lastError: "dispatcher interrupted");

        recovered.Should().Be(new OutboxClaimRecoveryResult(
            RetryScheduled: 0,
            DeadLettered: 1));
        var persisted = await GetMessageAsync(factory, message.Id);
        persisted.Status.Should().Be(OutboxMessageStatus.DeadLetter);
        persisted.AttemptCount.Should().Be(1);
        persisted.ClaimId.Should().BeNull();
        persisted.ClaimedAtUtc.Should().BeNull();
        persisted.ProcessedAtUtc.Should().BeNull();
        persisted.LastError.Should().Be("attempt limit exhausted");
        (await ClaimAsync(factory, claimedAtUtc.AddMinutes(2))).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Dead_letter_requires_the_active_claim_and_releases_later_incident_events()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var incidentId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
        var unavailable = CreateMessage(
            Guid.NewGuid(),
            incidentId,
            now.AddMinutes(-2),
            IncidentAlertEvents.ServerUnavailable);
        var recovered = CreateMessage(
            Guid.NewGuid(),
            incidentId,
            now.AddMinutes(-1),
            IncidentAlertEvents.ServerRecovered);
        await SeedAsync(factory, unavailable, recovered);
        var claim = await ClaimAsync(factory, now);
        claim.Should().NotBeNull();

        (await MarkDeadLetterAsync(
            factory,
            unavailable.Id,
            Guid.NewGuid(),
            "stale claim")).Should().BeFalse();
        (await MarkDeadLetterAsync(
            factory,
            unavailable.Id,
            claim!.ClaimId,
            "  permanent HTTP 400 response  ")).Should().BeTrue();

        var deadLetter = await GetMessageAsync(factory, unavailable.Id);
        deadLetter.Status.Should().Be(OutboxMessageStatus.DeadLetter);
        deadLetter.ClaimId.Should().BeNull();
        deadLetter.ClaimedAtUtc.Should().BeNull();
        deadLetter.ProcessedAtUtc.Should().BeNull();
        deadLetter.LastError.Should().Be("permanent HTTP 400 response");

        var nextClaim = await ClaimAsync(factory, now.AddSeconds(1));
        nextClaim.Should().NotBeNull();
        nextClaim!.Id.Should().Be(recovered.Id);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Statistics_and_retention_cover_pending_and_dead_letters_but_delete_only_one_processed_batch()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var now = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        var oldProcessed = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddDays(-50));
        var secondOldProcessed = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddDays(-45));
        var recentProcessed = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddDays(-5));
        await SeedAsync(factory, oldProcessed, secondOldProcessed, recentProcessed);

        var processedMessages = new[]
        {
            (oldProcessed, now.AddDays(-49)),
            (secondOldProcessed, now.AddDays(-44)),
            (recentProcessed, now.AddDays(-4))
        };
        foreach (var (message, processedAtUtc) in processedMessages)
        {
            var claim = await ClaimAsync(factory, now);
            claim.Should().NotBeNull();
            claim!.Id.Should().Be(message.Id);
            (await MarkProcessedAsync(
                factory,
                message.Id,
                claim.ClaimId,
                processedAtUtc)).Should().BeTrue();
        }

        var deadLetter = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-20));
        var pending = CreateMessage(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-10));
        await SeedAsync(factory, deadLetter, pending);
        var deadLetterClaim = await ClaimAsync(factory, now);
        deadLetterClaim.Should().NotBeNull();
        deadLetterClaim!.Id.Should().Be(deadLetter.Id);
        (await MarkDeadLetterAsync(
            factory,
            deadLetter.Id,
            deadLetterClaim.ClaimId,
            "permanent failure")).Should().BeTrue();

        var statistics = await GetStatisticsAsync(factory);
        statistics.Should().Be(new OutboxStatistics(
            PendingCount: 1,
            OldestPendingAtUtc: pending.OccurredAtUtc,
            DeadLetterCount: 1));

        var cutoffUtc = now.AddDays(-30);
        (await DeleteProcessedBatchOlderThanAsync(factory, cutoffUtc, batchSize: 1)).Should().Be(1);
        (await GetMessageOrDefaultAsync(factory, oldProcessed.Id)).Should().BeNull();
        (await GetMessageOrDefaultAsync(factory, secondOldProcessed.Id)).Should().NotBeNull();

        (await DeleteProcessedBatchOlderThanAsync(factory, cutoffUtc, batchSize: 1)).Should().Be(1);
        (await DeleteProcessedBatchOlderThanAsync(factory, cutoffUtc, batchSize: 1)).Should().Be(0);

        var remaining = await factory.ExecuteDbContextAsync(dbContext =>
            dbContext.OutboxMessages
                .AsNoTracking()
                .OrderBy(message => message.OccurredAtUtc)
                .ToListAsync());
        remaining.Select(message => message.Id).Should().BeEquivalentTo(
            [recentProcessed.Id, deadLetter.Id, pending.Id]);
        remaining.Single(message => message.Id == deadLetter.Id).Status
            .Should().Be(OutboxMessageStatus.DeadLetter);
        remaining.Single(message => message.Id == pending.Id).Status
            .Should().Be(OutboxMessageStatus.Pending);
    }

    private static OutboxMessage CreateMessage(
        Guid messageId,
        Guid incidentId,
        DateTimeOffset occurredAtUtc,
        string eventType = IncidentAlertEvents.ServerUnavailable) =>
        new(
            messageId,
            eventType,
            payloadVersion: 1,
            IncidentAlertEvents.AggregateType,
            incidentId,
            occurredAtUtc,
            payload: "{}");

    private static async Task SeedAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        params OutboxMessage[] messages)
    {
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.OutboxMessages.AddRange(messages);
            await dbContext.SaveChangesAsync();
        });
    }

    private static async Task<ClaimedOutboxMessage?> ClaimAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset claimedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None);
    }

    private static async Task<bool> MarkProcessedAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid messageId,
        Guid claimId,
        DateTimeOffset processedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.MarkProcessedAsync(
            messageId,
            claimId,
            processedAtUtc,
            CancellationToken.None);
    }

    private static async Task<bool> ScheduleRetryAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid messageId,
        Guid claimId,
        DateTimeOffset nextAttemptAtUtc,
        string lastError)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.ScheduleRetryAsync(
            messageId,
            claimId,
            nextAttemptAtUtc,
            lastError,
            CancellationToken.None);
    }

    private static async Task<OutboxClaimRecoveryResult> RecoverExpiredClaimsAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset expiredBeforeUtc,
        DateTimeOffset nextAttemptAtUtc,
        int maxAttempts,
        string lastError)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.RecoverExpiredClaimsAsync(
            expiredBeforeUtc,
            nextAttemptAtUtc,
            maxAttempts,
            lastError,
            "attempt limit exhausted",
            CancellationToken.None);
    }

    private static async Task<bool> MarkDeadLetterAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid messageId,
        Guid claimId,
        string lastError)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.MarkDeadLetterAsync(
            messageId,
            claimId,
            lastError,
            CancellationToken.None);
    }

    private static async Task<int> DeleteProcessedBatchOlderThanAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset cutoffUtc,
        int batchSize)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.DeleteProcessedBatchOlderThanAsync(
            cutoffUtc,
            batchSize,
            CancellationToken.None);
    }

    private static async Task<OutboxStatistics> GetStatisticsAsync(
        PostgreSqlGoldSrcOpsApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.GetStatisticsAsync(CancellationToken.None);
    }

    private static Task<OutboxMessage> GetMessageAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid messageId) =>
        factory.ExecuteDbContextAsync(dbContext =>
            dbContext.OutboxMessages
                .AsNoTracking()
                .SingleAsync(message => message.Id == messageId));

    private static Task<OutboxMessage?> GetMessageOrDefaultAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid messageId) =>
        factory.ExecuteDbContextAsync(dbContext =>
            dbContext.OutboxMessages
                .AsNoTracking()
                .SingleOrDefaultAsync(message => message.Id == messageId));
}
