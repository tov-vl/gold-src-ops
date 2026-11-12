using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class SnapshotRetentionWorkerTests
{
    [Theory]
    [InlineData("empty", 1)]
    [InlineData("full-batch", 1)]
    [InlineData("repository-error", 2)]
    [InlineData("shutdown", 0)]
    public async Task Worker_preserves_bounded_cleanup_and_separates_shutdown(string scenario, int expected)
    {
        var now = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        var clock = Mock.Of<IClock>(value => value.UtcNow == now);
        var options = new SnapshotRetentionOptions { CleanupInterval = TimeSpan.FromMilliseconds(1) };
        using var metrics = new SnapshotRetentionMetrics(options, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var repository = new Mock<IPollSnapshotRetentionRepository>();
        repository.Setup(value => value.DeleteBatchOlderThanAsync(now - options.RetentionPeriod, options.BatchSize, It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset _, int _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref calls) > 1 || string.Equals(scenario, "shutdown", StringComparison.Ordinal))
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                if (string.Equals(scenario, "repository-error", StringComparison.Ordinal))
                    throw new InvalidOperationException("private fixture");
                return string.Equals(scenario, "full-batch", StringComparison.Ordinal) ? options.BatchSize : 0;
            });
        var service = new SnapshotRetentionService(repository.Object, clock, new SnapshotRetentionSettings(options.RetentionPeriod, options.BatchSize));
        await using var provider = new ServiceCollection().AddScoped(_ => service).BuildServiceProvider();
        using var worker = new SnapshotRetentionBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            options, NullLogger<SnapshotRetentionBackgroundService>.Instance, metrics);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            collector.CollectObservableMetrics();
            Read("state").Should().Be(expected);
            Read("in_progress").Should().Be(1);
            Read("consecutive_failures").Should().Be(expected == 2 ? 1 : 0);
            Read("last_successful_cycle_timestamp").Should().Be(expected == 1 ? (now - DateTimeOffset.UnixEpoch).TotalSeconds : 0);
            repository.Verify(value => value.DeleteBatchOlderThanAsync(now - options.RetentionPeriod, options.BatchSize, It.IsAny<CancellationToken>()),
                Times.Exactly(string.Equals(scenario, "shutdown", StringComparison.Ordinal) ? 1 : 2));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        collector.CollectObservableMetrics();
        Read("in_progress").Should().Be(0);
        Read("last_failure_timestamp").Should().Be(expected == 2 ? (now - DateTimeOffset.UnixEpoch).TotalSeconds : 0);
        if (string.Equals(scenario, "shutdown", StringComparison.Ordinal))
            Read("last_finished_timestamp").Should().Be(0);

        return;

        double Read(string name) => collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.snapshot_retention.worker." + name, StringComparison.Ordinal)).Value;
    }
}
