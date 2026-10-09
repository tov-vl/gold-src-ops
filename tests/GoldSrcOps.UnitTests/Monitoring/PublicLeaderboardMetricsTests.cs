using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Credentials;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Infrastructure.Commands;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicLeaderboardMetricsTests
{
    [Fact]
    public void Success_failure_expiry_and_recovery_preserve_truthful_freshness()
    {
        var clock = new FixtureClock();
        var store = new PublicLeaderboardStore(clock);
        using var metrics = new PublicLeaderboardMetrics(new(true, Guid.NewGuid()), store, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, x => ReferenceEquals(x.Meter.Scope, store));
        double Gauge(string name) => collector.Measurements.Last(x => string.Equals(x.Name, "goldsrcops.leaderboard." + name, StringComparison.Ordinal)).Value;
        void Collect() => collector.CollectObservableMetrics();

        Collect();
        Gauge("snapshot_available").Should().Be(0);
        Gauge("last_success_timestamp").Should().Be(0);
        store.Publish(new(clock.UtcNow, []));
        metrics.RecordAttempt(PublicLeaderboardPollResult.Success, TimeSpan.FromMilliseconds(25));
        Collect();
        var success = Gauge("last_success_timestamp");
        Gauge("snapshot_available").Should().Be(1);
        Gauge("rows").Should().Be(0);
        clock.UtcNow += TimeSpan.FromMinutes(4);
        store.RecordFailure();
        metrics.RecordAttempt(PublicLeaderboardPollResult.Timeout, TimeSpan.FromSeconds(3));
        Collect();
        Gauge("last_success_timestamp").Should().Be(success);
        Gauge("last_attempt_timestamp").Should().Be(success + 240);
        Gauge("snapshot_age").Should().Be(240);
        clock.UtcNow += TimeSpan.FromMinutes(1);
        Collect();
        Gauge("observed_timestamp").Should().Be(success + 300);
        Gauge("snapshot_age").Should().Be(300);
        metrics.RecordAttempt(PublicLeaderboardPollResult.OperatorBusy, TimeSpan.Zero);
        store.RecordFailure(sourceUnavailable: true);
        Collect();
        Gauge("snapshot_available").Should().Be(0);
        Gauge("snapshot_age").Should().Be(0);
        Gauge("last_success_timestamp").Should().Be(success);
        store.Publish(new(clock.UtcNow, []));
        metrics.RecordAttempt(PublicLeaderboardPollResult.Success, TimeSpan.FromMilliseconds(20));
        Collect();
        Gauge("last_success_timestamp").Should().Be(success + 300);
        clock.UtcNow += TimeSpan.FromDays(2);
        Collect();
        Gauge("snapshot_available").Should().Be(0);
        collector.Measurements.Where(x => x.Tags.Count > 0).Should().OnlyContain(x =>
            x.Tags.Count == 1 && (x.Tags.ContainsKey("state") || x.Tags.ContainsKey("result")));
        collector.Measurements.Where(x => string.Equals(x.Name, "goldsrcops.leaderboard.poll_attempts", StringComparison.Ordinal))
            .Select(x => x.Tags["result"]).Should().Equal("success", "timeout", "operator_busy", "success");
    }

    [Fact]
    public void Disabled_monitor_has_live_heartbeat_without_exposing_cached_rows()
    {
        var clock = new FixtureClock();
        var store = new PublicLeaderboardStore(clock);
        store.Publish(new(clock.UtcNow, [new(1, "fixture", 3, 4, 0)]));
        using var metrics = new PublicLeaderboardMetrics(new(false, Guid.Empty), store, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, x => ReferenceEquals(x.Meter.Scope, store));
        collector.CollectObservableMetrics();
        foreach (var name in new[] { "enabled", "rows", "snapshot_available", "snapshot_age", "last_success_timestamp" })
        {
            collector.Measurements.Single(x => string.Equals(x.Name, "goldsrcops.leaderboard." + name, StringComparison.Ordinal)).Value.Should().Be(0);
        }
        collector.Measurements.Single(x => x.Tags.TryGetValue("state", out var state) && Equals(state, "disabled")).Value.Should().Be(1);
        var observed = collector.Measurements.Single(x => string.Equals(x.Name, "goldsrcops.leaderboard.observed_timestamp", StringComparison.Ordinal)).Value;
        observed.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("timeout", "Timeout")]
    [InlineData("auth", "AuthenticationFailed")]
    [InlineData("protocol", "InvalidFrame")]
    [InlineData("frame", "InvalidFrame")]
    [InlineData("other", "Failed")]
    [InlineData("canceled-pass", "Failed")]
    public void Failures_have_bounded_classification(string kind, string expected)
    {
        Exception error = kind switch
        {
            "timeout" => new TimeoutException("private fixture"),
            "auth" => new GoldSrcRconAuthenticationException(),
            "protocol" => new GoldSrcRconProtocolException("private fixture"),
            "frame" => new InvalidDataException("private fixture"),
            "canceled-pass" => new OperationCanceledException(),
            _ => new InvalidOperationException("private fixture")
        };
        PublicLeaderboardBackgroundService.FailureResult(error).ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_records_failure_but_shutdown_cancellation_is_not_an_attempt(bool shutdown)
    {
        var clock = new FixtureClock();
        var store = new PublicLeaderboardStore(clock);
        store.Publish(new(clock.UtcNow, []));
        var settings = new PublicLeaderboardSettings(true, Guid.NewGuid());
        using var metrics = new PublicLeaderboardMetrics(settings, store, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, x => ReferenceEquals(x.Meter.Scope, store));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var servers = new Mock<IServerRepository>(MockBehavior.Strict);
        servers.Setup(x => x.GetAsync(settings.ServerId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken token) =>
            {
                entered.SetResult();
                if (shutdown)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                throw new TimeoutException("private fixture");
            });
        var poller = new PublicLeaderboardPoller(settings, servers.Object,
            Mock.Of<IServerCredentialRepository>(), new UnusedSecrets(), new UnusedRcon(), store, clock);
        await using var provider = new ServiceCollection().AddScoped(_ => poller).BuildServiceProvider();
        using var worker = new PublicLeaderboardBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            settings, store, metrics, NullLogger<PublicLeaderboardBackgroundService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!shutdown)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!collector.Measurements.Any(x => string.Equals(x.Name, "goldsrcops.leaderboard.poll_attempts", StringComparison.Ordinal)))
            {
                await Task.Delay(10, deadline.Token);
            }
        }
        await worker.StopAsync(CancellationToken.None);
        var attempts = collector.Measurements.Where(x => string.Equals(x.Name, "goldsrcops.leaderboard.poll_attempts", StringComparison.Ordinal)).ToArray();
        if (shutdown)
        {
            attempts.Should().BeEmpty();
            store.Read().State.Should().Be("fresh");
        }
        else
        {
            attempts.Should().ContainSingle().Which.Tags["result"].Should().Be("timeout");
            store.Read().State.Should().Be("stale");
        }
    }

    private sealed class UnusedSecrets : ISecretReferenceResolver
    {
        public Task<SecretReferenceResolutionResult> ResolveAsync(string secretReference, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected credential access.");
    }

    private sealed class UnusedRcon : IGoldSrcRconClient
    {
        public Task<string> ExecuteAsync(GoldSrcRconRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected RCON access.");
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }
}
