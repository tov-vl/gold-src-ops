using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PublicStatusFreshnessTests
{
    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    [Theory]
    [InlineData(30, 69, true)]
    [InlineData(30, 70, true)]
    [InlineData(30, 71, false)]
    [InlineData(120, 249, true)]
    [InlineData(120, 250, true)]
    [InlineData(120, 251, false)]
    public async Task Summary_uses_each_servers_poll_interval(int interval, int ageSeconds, bool fresh)
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var repository = new Mock<IMonitoringReadRepository>();
        repository.Setup(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None))
            .ReturnsAsync([
                TestData.DashboardServerStatus(ServerStatus.Online, lastCheckedAtUtc: now),
                TestData.DashboardServerStatus(ServerStatus.Online,
                    lastCheckedAtUtc: now.AddSeconds(-ageSeconds), pollIntervalSeconds: interval)
            ]);
        var sut = new MonitoringReadService(repository.Object, new TestClock(now));

        var result = await sut.GetPublicStatusAsync(CancellationToken.None);

        result.State.Should().Be(fresh ? PublicStatusState.Operational : PublicStatusState.Degraded);
        result.OnlineServers.Should().Be(fresh ? 2 : 1);
        result.ServersRequiringAttention.Should().Be(fresh ? 0 : 1);
        result.LastObservedAtUtc.Should().Be(now);
    }

    [Theory]
    [InlineData(ServerStatus.Online)]
    [InlineData(ServerStatus.Offline)]
    [InlineData(ServerStatus.Unknown)]
    public async Task Summary_without_fresh_observations_is_unknown(ServerStatus storedStatus)
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var repository = new Mock<IMonitoringReadRepository>();
        repository.Setup(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None))
            .ReturnsAsync([
                TestData.DashboardServerStatus(storedStatus, lastCheckedAtUtc: now.AddSeconds(-71)),
                TestData.DashboardServerStatus(ServerStatus.Online, isEnabled: false, lastCheckedAtUtc: now),
                TestData.DashboardServerStatus(ServerStatus.Unknown)
            ]);
        var sut = new MonitoringReadService(repository.Object, new TestClock(now));

        var result = await sut.GetPublicStatusAsync(CancellationToken.None);

        result.State.Should().Be(PublicStatusState.Unknown);
        result.MonitoredServers.Should().Be(2);
        result.OnlineServers.Should().Be(0);
        result.ServersRequiringAttention.Should().Be(2);
        result.LastObservedAtUtc.Should().Be(now.AddSeconds(-71));
    }

    [Fact]
    public async Task Summary_expires_without_new_polls_and_recovers_after_observation()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var repository = new Mock<IMonitoringReadRepository>();
        List<DashboardServerStatusDto> states =
        [
            TestData.DashboardServerStatus(ServerStatus.Online, lastCheckedAtUtc: now)
        ];
        repository.Setup(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None))
            .ReturnsAsync(states);
        var sut = new MonitoringReadService(repository.Object, clock);

        (await sut.GetPublicStatusAsync(CancellationToken.None)).State.Should().Be(PublicStatusState.Operational);
        clock.UtcNow = now.AddSeconds(71);
        (await sut.GetPublicStatusAsync(CancellationToken.None)).State.Should().Be(PublicStatusState.Unknown);
        states[0] = states[0] with { LastCheckedAtUtc = clock.UtcNow };
        (await sut.GetPublicStatusAsync(CancellationToken.None)).State.Should().Be(PublicStatusState.Operational);
    }
}
