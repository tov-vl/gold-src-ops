using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.ProviderDelivery;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.AlertReceiver.Tests.ProviderDelivery;

public sealed class ProviderDispatcherTests
{
    [Fact]
    public void Provider_delivery_is_fail_closed_until_explicitly_enabled()
    {
        new ProviderDeliveryOptions().IsValid("Production").Should().BeTrue();
        new ProviderDeliveryOptions { Enabled = true }.IsValid("Production").Should().BeFalse();
        new ProviderDeliveryOptions
        {
            Enabled = true,
            Endpoint = "http://provider.invalid/deliver",
            Authorization = "Bearer secret",
        }.IsValid("Production").Should().BeFalse();
        new ProviderDeliveryOptions
        {
            Enabled = true,
            Endpoint = "https://provider.invalid/deliver",
            Authorization = "Bearer secret",
        }.IsValid("Production").Should().BeTrue();
    }

    [Fact]
    public async Task Retryable_failure_is_scheduled_before_attempt_limit()
    {
        var store = new StubStore(CreateMessage(attemptCount: 2));
        var dispatcher = CreateDispatcher(
            store,
            ProviderDeliveryResult.Retryable("Synthetic retry."),
            maxAttempts: 3);

        var result = await dispatcher.DispatchNextAsync(CancellationToken.None);

        result.Should().Be(ProviderDispatchResult.RetryScheduled);
        store.RetryScheduled.Should().BeTrue();
        store.DeadLettered.Should().BeFalse();
    }

    [Fact]
    public async Task Retryable_failure_at_attempt_limit_is_dead_lettered()
    {
        var store = new StubStore(CreateMessage(attemptCount: 3));
        var dispatcher = CreateDispatcher(
            store,
            ProviderDeliveryResult.Retryable("Synthetic retry."),
            maxAttempts: 3);

        var result = await dispatcher.DispatchNextAsync(CancellationToken.None);

        result.Should().Be(ProviderDispatchResult.DeadLettered);
        store.DeadLettered.Should().BeTrue();
        store.RetryScheduled.Should().BeFalse();
    }

    private static ProviderDispatcher CreateDispatcher(
        IProviderOutboxStore store,
        ProviderDeliveryResult deliveryResult,
        int maxAttempts) =>
        new(
            store,
            new StubChannel(deliveryResult),
            new FixedRetryDelayProvider(),
            TimeProvider.System,
            Options.Create(new ProviderDeliveryOptions { MaxAttempts = maxAttempts }));

    private static ClaimedProviderOutboxMessage CreateMessage(int attemptCount)
    {
        using var payload = JsonDocument.Parse("{}");
        return new ClaimedProviderOutboxMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Trigger",
            DateTimeOffset.UtcNow,
            payload.RootElement.Clone(),
            attemptCount,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
    }

    private sealed class StubChannel(ProviderDeliveryResult result) : IProviderDeliveryChannel
    {
        public Task<ProviderDeliveryResult> DeliverAsync(
            ClaimedProviderOutboxMessage message,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FixedRetryDelayProvider : IProviderRetryDelayProvider
    {
        public TimeSpan GetDelay(int attemptCount) => TimeSpan.FromSeconds(1);
    }

    private sealed class StubStore(ClaimedProviderOutboxMessage message) : IProviderOutboxStore
    {
        public bool RetryScheduled { get; private set; }

        public bool DeadLettered { get; private set; }

        public Task<ClaimedProviderOutboxMessage?> ClaimNextAsync(
            DateTimeOffset claimedAtUtc,
            CancellationToken cancellationToken) => Task.FromResult<ClaimedProviderOutboxMessage?>(message);

        public Task<bool> MarkProcessedAsync(Guid messageId, Guid claimId, DateTimeOffset processedAtUtc, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> ScheduleRetryAsync(Guid messageId, Guid claimId, DateTimeOffset nextAttemptAtUtc, string error, CancellationToken cancellationToken)
        {
            RetryScheduled = true;
            return Task.FromResult(true);
        }

        public Task<bool> MarkDeadLetterAsync(Guid messageId, Guid claimId, DateTimeOffset deadLetteredAtUtc, string error, CancellationToken cancellationToken)
        {
            DeadLettered = true;
            return Task.FromResult(true);
        }

        public Task<ProviderClaimRecoveryResult> RecoverExpiredClaimsAsync(DateTimeOffset expiredBeforeUtc, DateTimeOffset recoveredAtUtc, int maxAttempts, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderClaimRecoveryResult(0, 0));

        public Task<int> DeleteProcessedBatchAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<ProviderOutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderOutboxStatistics(0, 0, 0, null));
    }
}
