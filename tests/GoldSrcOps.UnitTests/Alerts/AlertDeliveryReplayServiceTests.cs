using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class AlertDeliveryReplayServiceTests
{
    [Theory]
    [AutoMoqData]
    public async Task ReplayAsync_normalizes_operator_input_and_uses_utc_clock(
        Guid requestId,
        Guid eventId,
        [Frozen] Mock<IAlertDeliveryReplayRepository> repository,
        [Frozen] Mock<IClock> clock,
        AlertDeliveryReplayService sut)
    {
        var now = new DateTimeOffset(2026, 8, 26, 18, 30, 0, TimeSpan.FromHours(3));
        var expected = DeadLetterReplayResult.EventNotFound();
        clock.SetupGet(static value => value.UtcNow).Returns(now);
        repository
            .Setup(x => x.ReplayAsync(
                requestId,
                eventId,
                "operator@example.test",
                now.ToUniversalTime(),
                "downstream endpoint was corrected",
                CancellationToken.None))
            .ReturnsAsync(expected);

        var result = await sut.ReplayAsync(
            new DeadLetterReplayCommand(
                requestId,
                eventId,
                " operator@example.test ",
                " downstream endpoint was corrected "),
            CancellationToken.None);

        result.Should().BeSameAs(expected);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task GetReplayAsync_delegates_to_repository(
        Guid requestId,
        DeadLetterReplayRecordDto expected,
        [Frozen] Mock<IAlertDeliveryReplayRepository> repository,
        AlertDeliveryReplayService sut)
    {
        repository
            .Setup(x => x.GetReplayAsync(requestId, CancellationToken.None))
            .ReturnsAsync(expected);

        var result = await sut.GetReplayAsync(requestId, CancellationToken.None);

        result.Should().BeSameAs(expected);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeReason_rejects_empty_values(string? reason)
    {
        var success = AlertDeliveryReplayService.TryNormalizeReason(
            reason,
            out var normalizedReason);

        success.Should().BeFalse();
        normalizedReason.Should().BeEmpty();
    }

    [Fact]
    public void TryNormalizeReason_trims_a_valid_value()
    {
        var success = AlertDeliveryReplayService.TryNormalizeReason(
            " maintenance completed ",
            out var normalizedReason);

        success.Should().BeTrue();
        normalizedReason.Should().Be("maintenance completed");
    }

    [Fact]
    public async Task ReplayAsync_rejects_invalid_input_before_repository_call()
    {
        var repository = new Mock<IAlertDeliveryReplayRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var sut = new AlertDeliveryReplayService(
            repository.Object,
            clock.Object,
            NullLogger<AlertDeliveryReplayService>.Instance);

        var emptyRequestId = () => sut.ReplayAsync(
            new DeadLetterReplayCommand(
                Guid.Empty,
                Guid.NewGuid(),
                "operator",
                "reason"),
            CancellationToken.None);
        var longReason = () => sut.ReplayAsync(
            new DeadLetterReplayCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "operator",
                new string('r', AlertDeliveryReplayService.MaxReasonLength + 1)),
            CancellationToken.None);

        await emptyRequestId.Should().ThrowAsync<ArgumentException>();
        await longReason.Should().ThrowAsync<ArgumentException>();
        repository.VerifyNoOtherCalls();
        clock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DeadLetterReplayResultKind.Accepted, "accepted", 7)]
    [InlineData(DeadLetterReplayResultKind.Idempotent, "idempotent", 7)]
    [InlineData(DeadLetterReplayResultKind.EventNotFound, "invalid", null)]
    [InlineData(DeadLetterReplayResultKind.EventNotDeadLetter, "invalid", null)]
    [InlineData(DeadLetterReplayResultKind.NewerEventProcessing, "conflict", null)]
    [InlineData(DeadLetterReplayResultKind.IdempotencyConflict, "conflict", null)]
    [InlineData(DeadLetterReplayResultKind.EventNotReplayable, "invalid", null)]
    public async Task ReplayAsync_records_low_cardinality_outcome_and_safe_lifecycle_logs(
        DeadLetterReplayResultKind resultKind,
        string expectedOutcome,
        int? expectedReplayNumber)
    {
        const string requestedByMarker = "principal-claim-secret-marker";
        const string reasonMarker = "operator-reason-secret-marker";
        var requestId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        var result = CreateResult(
            resultKind,
            requestId,
            eventId,
            requestedByMarker,
            reasonMarker,
            now);
        var repository = new Mock<IAlertDeliveryReplayRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var logger = new CapturingLogger<AlertDeliveryReplayService>();
        repository
            .Setup(x => x.ReplayAsync(
                requestId,
                eventId,
                requestedByMarker,
                now,
                reasonMarker,
                CancellationToken.None))
            .ReturnsAsync(result);
        clock.SetupGet(static value => value.UtcNow).Returns(now);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);
        var sut = new AlertDeliveryReplayService(repository.Object, clock.Object, logger);

        var actual = await sut.ReplayAsync(
            new DeadLetterReplayCommand(
                requestId,
                eventId,
                requestedByMarker,
                reasonMarker),
            CancellationToken.None);

        actual.Should().BeSameAs(result);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.replay_requests" &&
            metric.Value == 1 &&
            HasTag(metric, "result", expectedOutcome));
        logger.Entries.Should().Contain(entry =>
            entry.EventId.Id == 3101 &&
            Equals(entry.Properties["RequestId"], requestId) &&
            Equals(entry.Properties["EventId"], eventId) &&
            Equals(entry.Properties["ReplayNumber"], null) &&
            Equals(entry.Properties["ReplayOutcome"], "started"));
        logger.Entries.Should().Contain(entry =>
            entry.EventId.Id == 3102 &&
            Equals(entry.Properties["RequestId"], requestId) &&
            Equals(entry.Properties["EventId"], eventId) &&
            Equals(entry.Properties["ReplayNumber"], expectedReplayNumber) &&
            Equals(entry.Properties["ReplayOutcome"], expectedOutcome) &&
            entry.Properties.ContainsKey("DurationMs"));
        FlattenLogs(logger).Should().NotContain(requestedByMarker);
        FlattenLogs(logger).Should().NotContain(reasonMarker);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReplayAsync_logs_ambiguous_outcome_when_cancellation_is_requested()
    {
        const string exceptionMarker = "cancellation-secret-marker";
        var requestId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var repository = new Mock<IAlertDeliveryReplayRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var logger = new CapturingLogger<AlertDeliveryReplayService>();
        repository
            .Setup(x => x.ReplayAsync(
                requestId,
                eventId,
                "operator",
                now,
                "reason",
                cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(exceptionMarker, cancellation.Token));
        clock.SetupGet(static value => value.UtcNow).Returns(now);
        var sut = new AlertDeliveryReplayService(repository.Object, clock.Object, logger);

        var act = () => sut.ReplayAsync(
            new DeadLetterReplayCommand(requestId, eventId, "operator", "reason"),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().Contain(entry =>
            entry.EventId.Id == 3103 &&
            entry.Level == LogLevel.Warning &&
            Equals(entry.Properties["RequestId"], requestId) &&
            Equals(entry.Properties["EventId"], eventId) &&
            Equals(entry.Properties["ReplayNumber"], null) &&
            Equals(entry.Properties["ReplayOutcome"], "ambiguous"));
        FlattenLogs(logger).Should().NotContain(exceptionMarker);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReplayAsync_logs_fault_without_exception_details()
    {
        const string exceptionMarker = "database-secret-marker";
        var requestId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        var repository = new Mock<IAlertDeliveryReplayRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var logger = new CapturingLogger<AlertDeliveryReplayService>();
        repository
            .Setup(x => x.ReplayAsync(
                requestId,
                eventId,
                "operator",
                now,
                "reason",
                CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException(exceptionMarker));
        clock.SetupGet(static value => value.UtcNow).Returns(now);
        var sut = new AlertDeliveryReplayService(repository.Object, clock.Object, logger);

        var act = () => sut.ReplayAsync(
            new DeadLetterReplayCommand(requestId, eventId, "operator", "reason"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Should().Contain(entry =>
            entry.EventId.Id == 3104 &&
            entry.Level == LogLevel.Error &&
            Equals(entry.Properties["RequestId"], requestId) &&
            Equals(entry.Properties["EventId"], eventId) &&
            Equals(entry.Properties["ReplayNumber"], null) &&
            Equals(entry.Properties["ReplayOutcome"], "faulted"));
        FlattenLogs(logger).Should().NotContain(exceptionMarker);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
    }

    private static DeadLetterReplayResult CreateResult(
        DeadLetterReplayResultKind kind,
        Guid requestId,
        Guid eventId,
        string requestedBy,
        string reason,
        DateTimeOffset requestedAtUtc)
    {
        var replay = new DeadLetterReplayRecordDto(
            requestId,
            eventId,
            requestedBy,
            requestedAtUtc,
            reason,
            ReplayNumber: 7,
            PreviousAttemptCount: 3,
            PreviousDeadLetteredAtUtc: requestedAtUtc.AddMinutes(-5),
            NextAttemptAtUtc: requestedAtUtc);

        return kind switch
        {
            DeadLetterReplayResultKind.Accepted => DeadLetterReplayResult.Accepted(replay),
            DeadLetterReplayResultKind.Idempotent => DeadLetterReplayResult.Idempotent(replay),
            DeadLetterReplayResultKind.EventNotFound => DeadLetterReplayResult.EventNotFound(),
            DeadLetterReplayResultKind.EventNotDeadLetter => DeadLetterReplayResult.EventNotDeadLetter(),
            DeadLetterReplayResultKind.NewerEventProcessing => DeadLetterReplayResult.NewerEventProcessing(),
            DeadLetterReplayResultKind.IdempotencyConflict => DeadLetterReplayResult.IdempotencyConflict(),
            DeadLetterReplayResultKind.EventNotReplayable => DeadLetterReplayResult.EventNotReplayable(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static string FlattenLogs(CapturingLogger<AlertDeliveryReplayService> logger) =>
        string.Join(
            ' ',
            logger.Entries
                .SelectMany(entry => entry.Properties.Values.Prepend(entry.Message))
                .Select(static value => value?.ToString()));

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);
}
