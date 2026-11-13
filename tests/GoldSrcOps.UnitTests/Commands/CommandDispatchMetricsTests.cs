using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure.Commands;
using GoldSrcOps.UnitTests.Helpers;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class CommandDispatchMetricsTests
{
    [Fact]
    public void Observation_budget_includes_configured_network_timeout_without_changing_it()
    {
        var options = new CommandDispatcherOptions { LoopDelay = TimeSpan.FromSeconds(60) };
        var rcon = new GoldSrcRconOptions { Timeout = TimeSpan.FromMinutes(10) };
        using var metrics = new CommandDispatchMetrics(options, rcon, new FixtureClock());
        metrics.CycleBudgetSeconds.Should().Be(1380);
        rcon.Timeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Success_repeated_failure_stall_and_recovery_keep_independent_progress()
    {
        var clock = new FixtureClock();
        var options = new CommandDispatcherOptions();
        using var metrics = new CommandDispatchMetrics(options, new GoldSrcRconOptions(), clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        metrics.CycleBudgetSeconds.Should().Be(300);
        Read("state").Should().Be(0);
        metrics.BeginCycle();
        Read("in_progress").Should().Be(1);
        clock.UtcNow += TimeSpan.FromSeconds(2);
        metrics.CompleteCycle(failed: false, TimeSpan.FromSeconds(2));
        Read("state").Should().Be(1);
        Read("in_progress").Should().Be(0);
        var successful = Read("last_successful_cycle_timestamp");
        for (var index = 1; index <= 2; index++)
        {
            clock.UtcNow += TimeSpan.FromSeconds(5);
            metrics.BeginCycle();
            metrics.CompleteCycle(failed: true, TimeSpan.FromMilliseconds(25));
            Read("consecutive_failures").Should().Be(index);
        }
        Read("state").Should().Be(2);
        Read("last_successful_cycle_timestamp").Should().Be(successful);
        Read("last_failure_timestamp").Should().Be(successful + 10);
        Read("last_cycle_duration").Should().Be(0.025);
        metrics.BeginCycle();
        clock.UtcNow += TimeSpan.FromSeconds(300);
        Read("state").Should().Be(2);
        clock.UtcNow += TimeSpan.FromSeconds(1);
        Read("state").Should().Be(4);
        Read("in_progress").Should().Be(1);
        metrics.CompleteCycle(failed: false, TimeSpan.FromSeconds(301));
        Read("state").Should().Be(1);
        Read("consecutive_failures").Should().Be(0);
        Read("last_failure_timestamp").Should().Be(successful + 10);
        metrics.WorkerStopped();
        Read("state").Should().Be(0);
        Read("in_progress").Should().Be(0);
        collector.Measurements.Should().OnlyContain(value => value.Tags.Count == 0);

        return;

        double Read(string name)
        {
            collector.CollectObservableMetrics();
            return collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.command_dispatch.worker." + name, StringComparison.Ordinal)).Value;
        }
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    public void Disabled_and_never_started_are_distinct(bool enabled, int expected)
    {
        var clock = new FixtureClock();
        var options = new CommandDispatcherOptions { Enabled = enabled, LoopDelay = TimeSpan.FromSeconds(10) };
        using var metrics = new CommandDispatchMetrics(options, new GoldSrcRconOptions(), clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        metrics.CycleBudgetSeconds.Should().Be(300);
        clock.UtcNow += TimeSpan.FromSeconds(301);
        collector.CollectObservableMetrics();
        collector.Measurements.Single(value => string.Equals(value.Name, "goldsrcops.command_dispatch.worker.state", StringComparison.Ordinal)).Value.Should().Be(expected);
    }

    [Fact]
    public void Restart_and_backward_clock_do_not_reuse_progress()
    {
        var clock = new FixtureClock();
        var options = new CommandDispatcherOptions();
        using (var previous = new CommandDispatchMetrics(options, new GoldSrcRconOptions(), clock))
        {
            previous.BeginCycle();
            previous.CompleteCycle(failed: false, TimeSpan.Zero);
        }
        clock.UtcNow += TimeSpan.FromSeconds(5);
        using var restarted = new CommandDispatchMetrics(options, new GoldSrcRconOptions(), clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        collector.CollectObservableMetrics();
        collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.command_dispatch.worker.last_finished_timestamp", StringComparison.Ordinal)).Value.Should().Be(0);
        restarted.BeginCycle();
        restarted.CompleteCycle(failed: false, TimeSpan.Zero);
        clock.UtcNow -= TimeSpan.FromSeconds(1);
        collector.CollectObservableMetrics();
        collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.command_dispatch.worker.state", StringComparison.Ordinal)).Value.Should().Be(0);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    }
}
