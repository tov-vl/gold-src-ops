using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class AlertDispatcherTests
{
    [Fact]
    public async Task DispatchNextAsync_marks_a_delivered_message_processed_and_logs_only_safe_fields()
    {
        const string payloadMarker = "payload-secret-marker";
        var claimedAtUtc = new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        var completedAtUtc = claimedAtUtc.AddSeconds(1);
        var message = CreateMessage(claimedAtUtc, attemptCount: 1, payloadMarker);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var channel = new Mock<IAlertDeliveryChannel>(MockBehavior.Strict);
        var retryDelays = new Mock<IAlertRetryDelayProvider>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var logger = new CapturingLogger<AlertDispatcher>();
        outbox
            .Setup(x => x.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None))
            .ReturnsAsync(message);
        channel
            .Setup(x => x.DeliverAsync(message, CancellationToken.None))
            .ReturnsAsync(AlertDeliveryAttemptResult.Delivered());
        outbox
            .Setup(x => x.MarkProcessedAsync(
                message.Id,
                message.ClaimId,
                completedAtUtc,
                CancellationToken.None))
            .ReturnsAsync(true);
        clock.SetupSequence(x => x.UtcNow)
            .Returns(claimedAtUtc)
            .Returns(completedAtUtc);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);
        var sut = CreateDispatcher(
            outbox,
            channel,
            retryDelays,
            clock,
            logger);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Should().Be(AlertDispatchAttemptResult.Delivered(message.Id));
        outbox.VerifyAll();
        outbox.VerifyNoOtherCalls();
        channel.VerifyAll();
        channel.VerifyNoOtherCalls();
        retryDelays.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
        logger.Entries.Should().Contain(entry =>
            entry.EventId.Id == 3001 &&
            Equals(entry.Properties["EventId"], message.Id) &&
            Equals(entry.Properties["ServerId"], GetServerId(message)) &&
            Equals(entry.Properties["IncidentId"], message.AggregateId));
        FlattenLogs(logger).Should().NotContain(payloadMarker);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.delivery_attempts" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "delivered"));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.delivered" &&
            metric.Value == 1);
    }

    [Fact]
    public async Task DispatchNextAsync_honors_a_bounded_retry_after_without_using_backoff()
    {
        var claimedAtUtc = new DateTimeOffset(2026, 8, 26, 11, 0, 0, TimeSpan.Zero);
        var completedAtUtc = claimedAtUtc.AddSeconds(2);
        var retryAfter = TimeSpan.FromSeconds(20);
        var message = CreateMessage(claimedAtUtc, attemptCount: 2);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var channel = new Mock<IAlertDeliveryChannel>(MockBehavior.Strict);
        var retryDelays = new Mock<IAlertRetryDelayProvider>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        outbox
            .Setup(x => x.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None))
            .ReturnsAsync(message);
        channel
            .Setup(x => x.DeliverAsync(message, CancellationToken.None))
            .ReturnsAsync(AlertDeliveryAttemptResult.RetryableFailure(
                AlertDeliveryFailureCategory.RemoteResponse,
                remoteStatusCode: 503,
                retryAfter));
        outbox
            .Setup(x => x.ScheduleRetryAsync(
                message.Id,
                message.ClaimId,
                completedAtUtc + retryAfter,
                "Webhook returned HTTP status 503.",
                CancellationToken.None))
            .ReturnsAsync(true);
        clock.SetupSequence(x => x.UtcNow)
            .Returns(claimedAtUtc)
            .Returns(completedAtUtc);
        var sut = CreateDispatcher(outbox, channel, retryDelays, clock);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Should().Be(AlertDispatchAttemptResult.RetryScheduled(
            message.Id,
            completedAtUtc + retryAfter));
        outbox.VerifyAll();
        channel.VerifyAll();
        retryDelays.VerifyNoOtherCalls();
        clock.VerifyAll();
    }

    [Fact]
    public async Task DispatchNextAsync_uses_backoff_and_sanitizes_unexpected_channel_failures()
    {
        const string exceptionMarker = "authorization-secret-marker";
        var claimedAtUtc = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var completedAtUtc = claimedAtUtc.AddSeconds(1);
        var retryDelay = TimeSpan.FromSeconds(13);
        var message = CreateMessage(claimedAtUtc, attemptCount: 2);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var channel = new Mock<IAlertDeliveryChannel>(MockBehavior.Strict);
        var retryDelays = new Mock<IAlertRetryDelayProvider>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var logger = new CapturingLogger<AlertDispatcher>();
        outbox
            .Setup(x => x.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None))
            .ReturnsAsync(message);
        channel
            .Setup(x => x.DeliverAsync(message, CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException(exceptionMarker));
        retryDelays.Setup(x => x.GetDelay(message.AttemptCount)).Returns(retryDelay);
        outbox
            .Setup(x => x.ScheduleRetryAsync(
                message.Id,
                message.ClaimId,
                completedAtUtc + retryDelay,
                "Webhook delivery failed unexpectedly.",
                CancellationToken.None))
            .ReturnsAsync(true);
        clock.SetupSequence(x => x.UtcNow)
            .Returns(claimedAtUtc)
            .Returns(completedAtUtc);
        var sut = CreateDispatcher(
            outbox,
            channel,
            retryDelays,
            clock,
            logger);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Kind.Should().Be(AlertDispatchAttemptResultKind.RetryScheduled);
        outbox.VerifyAll();
        channel.VerifyAll();
        retryDelays.VerifyAll();
        clock.VerifyAll();
        FlattenLogs(logger).Should().NotContain(exceptionMarker);
        logger.Entries.Should().Contain(entry =>
            entry.EventId.Id == 3002 &&
            Equals(entry.Properties["FailureCategory"], AlertDeliveryFailureCategory.Unexpected));
    }

    [Fact]
    public async Task DispatchNextAsync_dead_letters_a_retryable_failure_at_the_attempt_limit()
    {
        var claimedAtUtc = new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero);
        var message = CreateMessage(claimedAtUtc, attemptCount: 3);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var channel = new Mock<IAlertDeliveryChannel>(MockBehavior.Strict);
        var retryDelays = new Mock<IAlertRetryDelayProvider>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        outbox
            .Setup(x => x.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None))
            .ReturnsAsync(message);
        channel
            .Setup(x => x.DeliverAsync(message, CancellationToken.None))
            .ReturnsAsync(AlertDeliveryAttemptResult.RetryableFailure(
                AlertDeliveryFailureCategory.Timeout));
        outbox
            .Setup(x => x.MarkDeadLetterAsync(
                message.Id,
                message.ClaimId,
                claimedAtUtc.AddSeconds(1),
                "Webhook request timed out.",
                CancellationToken.None))
            .ReturnsAsync(true);
        clock.SetupSequence(x => x.UtcNow)
            .Returns(claimedAtUtc)
            .Returns(claimedAtUtc.AddSeconds(1));
        var sut = CreateDispatcher(
            outbox,
            channel,
            retryDelays,
            clock,
            settings: CreateSettings(maxAttempts: 3));

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Should().Be(AlertDispatchAttemptResult.DeadLettered(message.Id));
        outbox.VerifyAll();
        channel.VerifyAll();
        retryDelays.VerifyNoOtherCalls();
        clock.VerifyAll();
    }

    [Fact]
    public async Task DispatchNextAsync_dead_letters_a_permanent_failure_immediately()
    {
        var claimedAtUtc = new DateTimeOffset(2026, 8, 26, 14, 0, 0, TimeSpan.Zero);
        var message = CreateMessage(claimedAtUtc, attemptCount: 1);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var channel = new Mock<IAlertDeliveryChannel>(MockBehavior.Strict);
        var retryDelays = new Mock<IAlertRetryDelayProvider>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        outbox
            .Setup(x => x.ClaimNextPendingAsync(claimedAtUtc, CancellationToken.None))
            .ReturnsAsync(message);
        channel
            .Setup(x => x.DeliverAsync(message, CancellationToken.None))
            .ReturnsAsync(AlertDeliveryAttemptResult.PermanentFailure(
                AlertDeliveryFailureCategory.RemoteResponse,
                remoteStatusCode: 400));
        outbox
            .Setup(x => x.MarkDeadLetterAsync(
                message.Id,
                message.ClaimId,
                claimedAtUtc.AddSeconds(1),
                "Webhook returned HTTP status 400.",
                CancellationToken.None))
            .ReturnsAsync(true);
        clock.SetupSequence(x => x.UtcNow)
            .Returns(claimedAtUtc)
            .Returns(claimedAtUtc.AddSeconds(1));
        var sut = CreateDispatcher(outbox, channel, retryDelays, clock);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Should().Be(AlertDispatchAttemptResult.DeadLettered(message.Id));
        outbox.VerifyAll();
        channel.VerifyAll();
        retryDelays.VerifyNoOtherCalls();
        clock.VerifyAll();
    }

    [Fact]
    public async Task Maintenance_recovers_claims_updates_gauges_and_deletes_one_bounded_batch()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 26, 15, 0, 0, TimeSpan.Zero);
        var settings = CreateSettings(cleanupBatchSize: 5);
        var statistics = new OutboxStatistics(
            PendingCount: 17,
            OldestPendingAtUtc: nowUtc.AddMinutes(-3),
            DeadLetterCount: 2);
        var outbox = new Mock<IOutboxStore>(MockBehavior.Strict);
        var channel = new Mock<IAlertDeliveryChannel>(MockBehavior.Strict);
        var retryDelays = new Mock<IAlertRetryDelayProvider>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        outbox
            .Setup(x => x.RecoverExpiredClaimsAsync(
                nowUtc - settings.ClaimTimeout,
                nowUtc,
                settings.MaxAttempts,
                AlertDispatcher.ExpiredClaimFailureReason,
                AlertDispatcher.ExhaustedClaimFailureReason,
                CancellationToken.None))
            .ReturnsAsync(new OutboxClaimRecoveryResult(
                RetryScheduled: 2,
                DeadLettered: 1));
        outbox
            .Setup(x => x.DeleteProcessedBatchOlderThanAsync(
                nowUtc - settings.ProcessedRetentionPeriod,
                settings.CleanupBatchSize,
                CancellationToken.None))
            .ReturnsAsync(settings.CleanupBatchSize);
        outbox
            .Setup(x => x.GetStatisticsAsync(CancellationToken.None))
            .ReturnsAsync(statistics);
        clock.SetupSequence(x => x.UtcNow)
            .Returns(nowUtc)
            .Returns(nowUtc)
            .Returns(nowUtc);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);
        var sut = CreateDispatcher(
            outbox,
            channel,
            retryDelays,
            clock,
            settings: settings);

        var recovered = await sut.RecoverExpiredClaimsAsync(CancellationToken.None);
        var cleanup = await sut.CleanupProcessedAsync(CancellationToken.None);
        var refreshed = await sut.RefreshStatisticsAsync(CancellationToken.None);
        metrics.CollectObservableMetrics();

        recovered.Should().Be(new OutboxClaimRecoveryResult(
            RetryScheduled: 2,
            DeadLettered: 1));
        cleanup.Should().Be(new OutboxCleanupResult(
            nowUtc - settings.ProcessedRetentionPeriod,
            settings.CleanupBatchSize,
            BatchLimitReached: true));
        refreshed.Should().Be(statistics);
        outbox.VerifyAll();
        outbox.VerifyNoOtherCalls();
        channel.VerifyNoOtherCalls();
        retryDelays.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.claims_recovered" &&
            metric.Value == 3);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.dead_letters" &&
            metric.Value == 1);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.processed_deleted" &&
            metric.Value == settings.CleanupBatchSize);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.pending" &&
            metric.Value == statistics.PendingCount);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.oldest_pending_age" &&
            metric.Value == TimeSpan.FromMinutes(3).TotalSeconds);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.dead_letter_count" &&
            metric.Value == statistics.DeadLetterCount);
    }

    private static AlertDispatcher CreateDispatcher(
        Mock<IOutboxStore> outbox,
        Mock<IAlertDeliveryChannel> channel,
        Mock<IAlertRetryDelayProvider> retryDelays,
        Mock<IClock> clock,
        CapturingLogger<AlertDispatcher>? logger = null,
        AlertDispatcherSettings? settings = null) =>
        new(
            outbox.Object,
            channel.Object,
            retryDelays.Object,
            clock.Object,
            settings ?? CreateSettings(),
            logger ?? new CapturingLogger<AlertDispatcher>());

    private static AlertDispatcherSettings CreateSettings(
        int maxAttempts = 8,
        int cleanupBatchSize = 100) =>
        new(
            claimTimeout: TimeSpan.FromSeconds(30),
            maxAttempts,
            baseRetryDelay: TimeSpan.FromSeconds(5),
            maximumRetryDelay: TimeSpan.FromMinutes(5),
            processedRetentionPeriod: TimeSpan.FromDays(30),
            cleanupBatchSize);

    private static ClaimedOutboxMessage CreateMessage(
        DateTimeOffset claimedAtUtc,
        int attemptCount,
        string payloadMarker = "safe-reason")
    {
        var eventId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(
            new IncidentAlertEventV1(
                eventId,
                IncidentAlertEvents.ServerUnavailable,
                claimedAtUtc.AddMinutes(-1),
                incidentId,
                serverId,
                "Test server",
                payloadMarker,
                ConsecutiveFailures: 3,
                OpenedAtUtc: claimedAtUtc.AddMinutes(-1),
                ClosedAtUtc: null,
                DurationSeconds: null),
            JsonSerializerOptions.Web);

        return new ClaimedOutboxMessage(
            eventId,
            IncidentAlertEvents.ServerUnavailable,
            IncidentAlertEventV1.CurrentPayloadVersion,
            IncidentAlertEvents.AggregateType,
            incidentId,
            claimedAtUtc.AddMinutes(-1),
            payload,
            attemptCount,
            Guid.NewGuid(),
            claimedAtUtc);
    }

    private static Guid GetServerId(ClaimedOutboxMessage message) =>
        JsonSerializer.Deserialize<IncidentAlertEventV1>(
            message.Payload,
            JsonSerializerOptions.Web)!.ServerId;

    private static string FlattenLogs(CapturingLogger<AlertDispatcher> logger) =>
        string.Join(
            ' ',
            logger.Entries.SelectMany(entry =>
                entry.Properties.Values.Prepend(entry.Message)).Select(static value => value?.ToString()));

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);
}
