using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Monitoring;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class GoldSrcPollingWorkerTests
{
    [Theory]
    [InlineData("empty", 1)]
    [InlineData("all-a2s-failed", 1)]
    [InlineData("repository-error", 2)]
    [InlineData("shutdown", 0)]
    public async Task Worker_separates_cycle_progress_from_server_failures_and_shutdown(string scenario, int expected)
    {
        var clock = Mock.Of<IClock>(value => value.UtcNow == new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        var options = new GoldSrcPollingOptions { LoopDelay = TimeSpan.FromMilliseconds(1) };
        using var metrics = new GoldSrcPollingMetrics(options, clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var servers = new Mock<IServerRepository>();
        servers.Setup(value => value.ListEnabledAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken token) =>
            {
                if (Interlocked.Increment(ref calls) > 1 || string.Equals(scenario, "shutdown", StringComparison.Ordinal))
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                if (string.Equals(scenario, "repository-error", StringComparison.Ordinal)) throw new InvalidOperationException("private fixture");
                return string.Equals(scenario, "all-a2s-failed", StringComparison.Ordinal)
                    ? new[] { new Server("Fixture", GameServerKind.GoldSrc, new ServerEndpoint("127.0.0.1", 27015, rconPort: null), 1, notes: null, clock.UtcNow) }
                    : Array.Empty<Server>();
            });
        var queries = new Mock<IGoldSrcServerQueryClient>();
        queries.Setup(value => value.QueryInfoAsync(It.IsAny<GameServerEndpoint>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("private fixture"));
        var service = new ServerPollingService(servers.Object, Mock.Of<IIncidentRepository>(), Mock.Of<IOutboxWriter>(),
            Mock.Of<IUnitOfWork>(), queries.Object, clock, new ServerPollingSettings(options.QueryTimeout, options.BatchSize, options.IncidentFailureThreshold));
        await using var provider = new ServiceCollection().AddScoped(_ => service).BuildServiceProvider();
        using var worker = new GoldSrcPollingBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            options, NullLogger<GoldSrcPollingBackgroundService>.Instance, metrics);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            collector.CollectObservableMetrics();
            collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.polling.state", StringComparison.Ordinal)).Value.Should().Be(expected);
            collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.polling.last_failure_timestamp", StringComparison.Ordinal)).Value
                .Should().Be(expected == 2 ? (clock.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds : 0);
            if (string.Equals(scenario, "all-a2s-failed", StringComparison.Ordinal))
            {
                queries.Verify(value => value.QueryInfoAsync(It.IsAny<GameServerEndpoint>(), It.IsAny<CancellationToken>()), Times.Once);
                servers.Verify(value => value.AddSnapshotAsync(It.IsAny<PollSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        collector.CollectObservableMetrics();
        if (string.Equals(scenario, "shutdown", StringComparison.Ordinal))
            collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.polling.last_finished_timestamp", StringComparison.Ordinal)).Value.Should().Be(0);
    }
}
