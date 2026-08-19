using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class SnapshotRetentionServiceTests
{
    [Fact]
    public async Task CleanupAsync_deletes_one_bounded_batch_before_the_retention_cutoff()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var cutoffUtc = nowUtc.AddDays(-30);
        const int batchSize = 100;
        var repository = new Mock<IPollSnapshotRetentionRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        repository
            .Setup(x => x.DeleteBatchOlderThanAsync(cutoffUtc, batchSize, CancellationToken.None))
            .ReturnsAsync(batchSize);
        clock.SetupGet(x => x.UtcNow).Returns(nowUtc);
        var sut = new SnapshotRetentionService(
            repository.Object,
            clock.Object,
            new SnapshotRetentionSettings(TimeSpan.FromDays(30), batchSize));
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.CleanupAsync(CancellationToken.None);

        result.Should().Be(new SnapshotRetentionResult(
            cutoffUtc,
            DeletedSnapshots: batchSize,
            BatchLimitReached: true));
        repository.Verify(
            x => x.DeleteBatchOlderThanAsync(cutoffUtc, batchSize, CancellationToken.None),
            Times.Once);
        repository.VerifyNoOtherCalls();
        clock.VerifyGet(x => x.UtcNow, Times.Once);
        clock.VerifyNoOtherCalls();
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.snapshot_retention.runs" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "success"));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.snapshot_retention.snapshots_deleted" &&
            metric.Value == batchSize);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.snapshot_retention.duration" &&
            metric.Value >= 0 &&
            HasTag(metric, "result", "success"));
    }

    [Fact]
    public async Task CleanupAsync_reports_an_incomplete_batch_when_fewer_snapshots_are_deleted()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var cutoffUtc = nowUtc.AddDays(-7);
        var repository = new Mock<IPollSnapshotRetentionRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        repository
            .Setup(x => x.DeleteBatchOlderThanAsync(cutoffUtc, 50, CancellationToken.None))
            .ReturnsAsync(12);
        clock.SetupGet(x => x.UtcNow).Returns(nowUtc);
        var sut = new SnapshotRetentionService(
            repository.Object,
            clock.Object,
            new SnapshotRetentionSettings(TimeSpan.FromDays(7), batchSize: 50));

        var result = await sut.CleanupAsync(CancellationToken.None);

        result.BatchLimitReached.Should().BeFalse();
        result.DeletedSnapshots.Should().Be(12);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CleanupAsync_records_failure_and_rethrows_repository_errors()
    {
        var nowUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var cutoffUtc = nowUtc.AddDays(-30);
        var repository = new Mock<IPollSnapshotRetentionRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        repository
            .Setup(x => x.DeleteBatchOlderThanAsync(cutoffUtc, 100, CancellationToken.None))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        clock.SetupGet(x => x.UtcNow).Returns(nowUtc);
        var sut = new SnapshotRetentionService(
            repository.Object,
            clock.Object,
            new SnapshotRetentionSettings(TimeSpan.FromDays(30), batchSize: 100));
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var act = async () => await sut.CleanupAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("database unavailable");
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyAll();
        clock.VerifyNoOtherCalls();
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.snapshot_retention.runs" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "failure"));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.snapshot_retention.duration" &&
            metric.Value >= 0 &&
            HasTag(metric, "result", "failure"));
    }

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);
}
