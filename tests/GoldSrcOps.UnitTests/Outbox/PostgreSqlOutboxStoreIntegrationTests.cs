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
            "dispatcher interrupted")).Should().Be(0);

        var retryAtUtc = claimedAtUtc.AddMinutes(1);
        (await RecoverExpiredClaimsAsync(
            factory,
            claimedAtUtc.AddSeconds(1),
            retryAtUtc,
            "  dispatcher interrupted  ")).Should().Be(1);
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

    private static async Task<int> RecoverExpiredClaimsAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset expiredBeforeUtc,
        DateTimeOffset nextAttemptAtUtc,
        string lastError)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        return await store.RecoverExpiredClaimsAsync(
            expiredBeforeUtc,
            nextAttemptAtUtc,
            lastError,
            CancellationToken.None);
    }

    private static Task<OutboxMessage> GetMessageAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid messageId) =>
        factory.ExecuteDbContextAsync(dbContext =>
            dbContext.OutboxMessages
                .AsNoTracking()
                .SingleAsync(message => message.Id == messageId));
}
