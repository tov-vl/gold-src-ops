using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.UnitTests.Helpers;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class GoldSrcPollingMetricsTests
{
    [Fact]
    public void Unknown_success_failure_stall_and_recovery_keep_independent_progress()
    {
        var clock = new FixtureClock();
        var options = new GoldSrcPollingOptions();
        using var metrics = new GoldSrcPollingMetrics(options, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        metrics.CycleBudgetSeconds.Should().Be(370);
        Read("state").Should().Be(0);
        Read("last_finished_timestamp").Should().Be(0);
        metrics.BeginCycle();
        clock.UtcNow += TimeSpan.FromSeconds(2);
        metrics.CompleteCycle(failed: false, TimeSpan.FromSeconds(2));
        Read("state").Should().Be(1);
        var successful = Read("last_successful_cycle_timestamp");
        clock.UtcNow += TimeSpan.FromSeconds(5);
        metrics.BeginCycle();
        metrics.CompleteCycle(failed: true, TimeSpan.FromMilliseconds(25));
        Read("state").Should().Be(2);
        Read("last_successful_cycle_timestamp").Should().Be(successful);
        Read("last_failure_timestamp").Should().Be(successful + 5);
        Read("last_cycle_duration").Should().Be(0.025);
        metrics.BeginCycle();
        clock.UtcNow += TimeSpan.FromSeconds(metrics.CycleBudgetSeconds);
        Read("state").Should().Be(2);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        Read("state").Should().Be(4);
        Read("observed_timestamp").Should().Be(successful + 376);
        metrics.CompleteCycle(failed: false, TimeSpan.FromSeconds(371));
        Read("state").Should().Be(1);
        Read("last_failure_timestamp").Should().Be(successful + 5);
        collector.Measurements.Should().OnlyContain(value => value.Tags.Count == 0);

        return;

        double Read(string name)
        {
            collector.CollectObservableMetrics();
            return collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.polling." + name, StringComparison.Ordinal)).Value;
        }
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public void Disabled_and_never_started_are_distinct(bool enabled, int expected)
    {
        var clock = new FixtureClock();
        var options = new GoldSrcPollingOptions { Enabled = enabled, BatchSize = 1 };
        using var metrics = new GoldSrcPollingMetrics(options, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        metrics.CycleBudgetSeconds.Should().Be(300);
        clock.UtcNow += TimeSpan.FromSeconds(301);
        collector.CollectObservableMetrics();
        collector.Measurements.Single(value => string.Equals(value.Name, "goldsrcops.polling.state", StringComparison.Ordinal)).Value.Should().Be(expected);
        collector.Measurements.Single(value => string.Equals(value.Name, "goldsrcops.polling.enabled", StringComparison.Ordinal)).Value.Should().Be(enabled ? 1 : 0);
    }

    [Fact]
    public void Restart_and_backward_clock_do_not_reuse_progress()
    {
        var clock = new FixtureClock();
        var options = new GoldSrcPollingOptions();
        using (var previous = new GoldSrcPollingMetrics(options, clock))
        {
            previous.BeginCycle();
            previous.CompleteCycle(failed: false, TimeSpan.Zero);
            previous.WorkerStopped();
        }
        clock.UtcNow += TimeSpan.FromSeconds(5);
        using var restarted = new GoldSrcPollingMetrics(options, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        restarted.BeginCycle();
        restarted.CompleteCycle(failed: false, TimeSpan.Zero);
        clock.UtcNow -= TimeSpan.FromSeconds(1);
        collector.CollectObservableMetrics();
        collector.Measurements.Single(value => string.Equals(value.Name, "goldsrcops.polling.state", StringComparison.Ordinal)).Value.Should().Be(0);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    }
}
