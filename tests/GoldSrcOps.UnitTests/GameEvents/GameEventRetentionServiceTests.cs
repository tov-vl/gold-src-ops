using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.GameEvents;

public sealed class GameEventRetentionServiceTests
{
    [Fact]
    public async Task CleanupAsync_deletes_one_bounded_batch_by_received_time()
    {
        var nowUtc = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        var cutoffUtc = nowUtc.AddDays(-45);
        var repository = new Mock<IGameEventInboxRetentionRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        repository
            .Setup(x => x.DeleteBatchReceivedBeforeAsync(
                cutoffUtc,
                100,
                CancellationToken.None))
            .ReturnsAsync(100);
        clock.SetupGet(x => x.UtcNow).Returns(nowUtc);
        var sut = new GameEventRetentionService(
            repository.Object,
            clock.Object,
            new GameEventRetentionSettings(TimeSpan.FromDays(45), batchSize: 100));
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.CleanupAsync(CancellationToken.None);

        result.Should().Be(new GameEventRetentionResult(
            cutoffUtc,
            DeletedEvents: 100,
            BatchLimitReached: true));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.game_events.retention_runs" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "success"));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.game_events.deleted" &&
            metric.Value == 100);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupAsync_records_failure_and_rethrows_repository_errors()
    {
        var nowUtc = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        var cutoffUtc = nowUtc.AddDays(-45);
        var repository = new Mock<IGameEventInboxRetentionRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        repository
            .Setup(x => x.DeleteBatchReceivedBeforeAsync(
                cutoffUtc,
                100,
                CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        clock.SetupGet(x => x.UtcNow).Returns(nowUtc);
        var sut = new GameEventRetentionService(
            repository.Object,
            clock.Object,
            new GameEventRetentionSettings(TimeSpan.FromDays(45), batchSize: 100));
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var act = async () => await sut.CleanupAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("database unavailable");
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.game_events.retention_runs" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "failure"));
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
    }

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);
}
