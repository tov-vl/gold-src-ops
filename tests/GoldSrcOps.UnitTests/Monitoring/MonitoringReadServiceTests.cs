using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class MonitoringReadServiceTests
{
    [Fact]
    public async Task GetPublicA2sHistoryAsync_fills_missing_hourly_buckets_and_aggregates_observed_samples()
    {
        var expectedToUtc = new DateTimeOffset(2026, 9, 7, 12, 30, 0, TimeSpan.Zero);
        // Seven ticks are below PostgreSQL's one-microsecond timestamp resolution.
        var now = expectedToUtc.AddTicks(7);
        var fromUtc = expectedToUtc.AddHours(-24);
        var repository = new Mock<IMonitoringReadRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        IReadOnlyList<PublicA2sBucketCountDto> counts =
        [
            new(fromUtc, SampleCount: 2, ReachableSampleCount: 1),
            new(fromUtc.AddHours(2), SampleCount: 2, ReachableSampleCount: 0),
            new(fromUtc.AddHours(23), SampleCount: 3, ReachableSampleCount: 3)
        ];
        clock.SetupGet(static x => x.UtcNow).Returns(now);
        repository
            .Setup(x => x.ListPublicA2sBucketCountsAsync(
                fromUtc,
                expectedToUtc,
                TimeSpan.FromHours(1),
                CancellationToken.None))
            .ReturnsAsync(counts);
        var sut = new MonitoringReadService(repository.Object, clock.Object);

        var result = await sut.GetPublicA2sHistoryAsync(
            PublicA2sHistoryWindow.Last24Hours,
            CancellationToken.None);

        result.Window.Should().Be(PublicA2sHistoryWindow.Last24Hours);
        result.FromUtc.Should().Be(fromUtc);
        result.ToUtc.Should().Be(expectedToUtc);
        result.BucketMinutes.Should().Be(60);
        result.ObservedBuckets.Should().Be(3);
        result.TotalBuckets.Should().Be(24);
        result.ObservedReachabilityPercent.Should().Be(57.1m);
        result.Buckets.Should().HaveCount(24);
        result.Buckets[0].Should().BeEquivalentTo(new PublicA2sBucketDto(
            fromUtc,
            PublicA2sBucketState.Degraded,
            50m));
        result.Buckets[1].Should().BeEquivalentTo(new PublicA2sBucketDto(
            fromUtc.AddHours(1),
            PublicA2sBucketState.Unknown,
            ObservedReachabilityPercent: null));
        result.Buckets[2].Should().BeEquivalentTo(new PublicA2sBucketDto(
            fromUtc.AddHours(2),
            PublicA2sBucketState.Unreachable,
            0m));
        result.Buckets[^1].Should().BeEquivalentTo(new PublicA2sBucketDto(
            fromUtc.AddHours(23),
            PublicA2sBucketState.Operational,
            100m));
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyGet(static x => x.UtcNow, Times.Once);
        clock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetPublicA2sHistoryAsync_uses_twenty_eight_six_hour_buckets_for_seven_days()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 30, 0, TimeSpan.Zero);
        var fromUtc = now.AddDays(-7);
        var repository = new Mock<IMonitoringReadRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        clock.SetupGet(static x => x.UtcNow).Returns(now);
        repository
            .Setup(x => x.ListPublicA2sBucketCountsAsync(
                fromUtc,
                now,
                TimeSpan.FromHours(6),
                CancellationToken.None))
            .ReturnsAsync([]);
        var sut = new MonitoringReadService(repository.Object, clock.Object);

        var result = await sut.GetPublicA2sHistoryAsync(
            PublicA2sHistoryWindow.Last7Days,
            CancellationToken.None);

        result.BucketMinutes.Should().Be(360);
        result.ObservedBuckets.Should().Be(0);
        result.TotalBuckets.Should().Be(28);
        result.ObservedReachabilityPercent.Should().BeNull();
        result.Buckets.Should().HaveCount(28)
            .And.OnlyContain(static bucket =>
                bucket.State == PublicA2sBucketState.Unknown &&
                bucket.ObservedReachabilityPercent == null);
        result.Buckets[0].StartedAtUtc.Should().Be(fromUtc);
        result.Buckets[^1].StartedAtUtc.Should().Be(now.AddHours(-6));
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
        clock.VerifyGet(static x => x.UtcNow, Times.Once);
        clock.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task GetDashboardOverviewAsync_counts_server_statuses_and_open_incidents(
        [Frozen] Mock<IMonitoringReadRepository> repository,
        MonitoringReadService sut)
    {
        var now = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        IReadOnlyList<DashboardServerStatusDto> serverStatuses =
        [
            TestData.DashboardServerStatus(ServerStatus.Online, lastCheckedAtUtc: now.AddMinutes(-5)),
            TestData.DashboardServerStatus(ServerStatus.Offline, lastCheckedAtUtc: now),
            TestData.DashboardServerStatus(ServerStatus.Unknown, isEnabled: false)
        ];
        repository
            .Setup(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None))
            .ReturnsAsync(serverStatuses);
        repository
            .Setup(static x => x.CountOpenIncidentsAsync(CancellationToken.None))
            .ReturnsAsync(2);

        var result = await sut.GetDashboardOverviewAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(new DashboardOverviewDto(
            TotalServers: 3,
            EnabledServers: 2,
            DisabledServers: 1,
            OnlineServers: 1,
            OfflineServers: 1,
            UnknownServers: 1,
            OpenIncidents: 2,
            LastCheckedAtUtc: now));
        repository.Verify(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None), Times.Once);
        repository.Verify(static x => x.CountOpenIncidentsAsync(CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task GetFleetOverviewAsync_marks_stale_and_incident_rows_and_aggregates_one_projection(
        [Frozen] Mock<IMonitoringReadRepository> repository,
        [Frozen] Mock<IClock> clock,
        MonitoringReadService sut)
    {
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var healthyId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var offlineId = Guid.NewGuid();
        var pausedId = Guid.NewGuid();
        IReadOnlyList<FleetServerStateDto> states =
        [
            new(
                healthyId,
                "Healthy",
                GameServerKind.GoldSrc,
                "healthy.example.test",
                27015,
                true,
                30,
                ServerStatus.Online,
                now.AddSeconds(-20),
                18,
                "de_dust2",
                4,
                20,
                0,
                0,
                0),
            new(
                staleId,
                "Stale",
                GameServerKind.GoldSrc,
                "stale.example.test",
                27016,
                true,
                30,
                ServerStatus.Online,
                now.AddSeconds(-71),
                22,
                "de_inferno",
                6,
                20,
                1,
                0,
                0),
            new(
                offlineId,
                "Offline",
                GameServerKind.GoldSrc,
                "offline.example.test",
                27017,
                true,
                30,
                ServerStatus.Offline,
                now.AddSeconds(-10),
                null,
                null,
                null,
                null,
                null,
                3,
                1),
            new(
                pausedId,
                "Paused",
                GameServerKind.GoldSrc,
                "paused.example.test",
                27018,
                false,
                30,
                ServerStatus.Unknown,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                2)
        ];
        repository
            .Setup(static x => x.ListFleetServerStatesAsync(CancellationToken.None))
            .ReturnsAsync(states);
        clock.SetupGet(static x => x.UtcNow).Returns(now);

        var result = await sut.GetFleetOverviewAsync(CancellationToken.None);

        result.Overview.Should().BeEquivalentTo(new DashboardOverviewDto(
            TotalServers: 4,
            EnabledServers: 3,
            DisabledServers: 1,
            OnlineServers: 2,
            OfflineServers: 1,
            UnknownServers: 1,
            OpenIncidents: 3,
            LastCheckedAtUtc: now.AddSeconds(-10)));
        result.Servers.Should().HaveCount(4);
        result.Servers.Single(server => server.ServerId == healthyId)
            .Should().BeEquivalentTo(new
            {
                Bots = (int?)0,
                IsStale = false,
                RequiresAttention = false
            });
        result.Servers.Single(server => server.ServerId == staleId)
            .Should().BeEquivalentTo(new
            {
                Bots = (int?)1,
                IsStale = true,
                RequiresAttention = true
            });
        result.Servers.Single(server => server.ServerId == offlineId)
            .Should().BeEquivalentTo(new
            {
                IsStale = false,
                RequiresAttention = true
            });
        result.Servers.Single(server => server.ServerId == pausedId)
            .Should().BeEquivalentTo(new
            {
                IsStale = false,
                RequiresAttention = true
            });
        repository.Verify(static x => x.ListFleetServerStatesAsync(CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
        clock.VerifyGet(static x => x.UtcNow, Times.Once);
        clock.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task GetPublicStatusAsync_excludes_disabled_servers(
        [Frozen] Mock<IMonitoringReadRepository> repository,
        MonitoringReadService sut)
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<DashboardServerStatusDto> serverStatuses =
        [
            TestData.DashboardServerStatus(ServerStatus.Online, lastCheckedAtUtc: now),
            TestData.DashboardServerStatus(
                ServerStatus.Offline,
                isEnabled: false,
                lastCheckedAtUtc: now.AddMinutes(1))
        ];
        repository
            .Setup(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None))
            .ReturnsAsync(serverStatuses);
        repository
            .Setup(static x => x.CountOpenIncidentsForEnabledServersAsync(CancellationToken.None))
            .ReturnsAsync(0);

        var result = await sut.GetPublicStatusAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(new PublicStatusDto(
            State: PublicStatusState.Operational,
            MonitoredServers: 1,
            OnlineServers: 1,
            ServersRequiringAttention: 0,
            OpenIncidents: 0,
            LastObservedAtUtc: now));
        repository.Verify(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None), Times.Once);
        repository.Verify(static x => x.CountOpenIncidentsForEnabledServersAsync(CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task GetPublicStatusAsync_returns_unknown_without_observed_enabled_server(
        [Frozen] Mock<IMonitoringReadRepository> repository,
        MonitoringReadService sut)
    {
        IReadOnlyList<DashboardServerStatusDto> serverStatuses =
        [
            TestData.DashboardServerStatus(ServerStatus.Unknown)
        ];
        repository
            .Setup(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None))
            .ReturnsAsync(serverStatuses);
        repository
            .Setup(static x => x.CountOpenIncidentsForEnabledServersAsync(CancellationToken.None))
            .ReturnsAsync(1);

        var result = await sut.GetPublicStatusAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(new PublicStatusDto(
            State: PublicStatusState.Unknown,
            MonitoredServers: 1,
            OnlineServers: 0,
            ServersRequiringAttention: 1,
            OpenIncidents: 1,
            LastObservedAtUtc: null));
        repository.Verify(static x => x.ListDashboardServerStatusesAsync(CancellationToken.None), Times.Once);
        repository.Verify(static x => x.CountOpenIncidentsForEnabledServersAsync(CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task ListSnapshotsAsync_uses_default_limit_for_recent_history(
        [Frozen] Mock<IMonitoringReadRepository> repository,
        MonitoringReadService sut)
    {
        var serverId = Guid.NewGuid();
        var fromUtc = new DateTimeOffset(2026, 4, 25, 9, 0, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        IReadOnlyList<PollSnapshotDto> snapshots =
        [
            TestData.PollSnapshot(serverId, toUtc)
        ];
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(true);
        repository
            .Setup(x => x.ListSnapshotsAsync(
                serverId,
                fromUtc,
                toUtc,
                MonitoringReadService.DefaultSnapshotLimit,
                CancellationToken.None))
            .ReturnsAsync(snapshots);

        var result = await sut.ListSnapshotsAsync(serverId, fromUtc, toUtc, limit: null, CancellationToken.None);

        result.Should().BeEquivalentTo(new SnapshotHistoryDto(
            serverId,
            fromUtc,
            toUtc,
            MonitoringReadService.DefaultSnapshotLimit,
            snapshots));
        repository.Verify(x => x.ServerExistsAsync(serverId, CancellationToken.None), Times.Once);
        repository.Verify(x => x.ListSnapshotsAsync(
            serverId,
            fromUtc,
            toUtc,
            MonitoringReadService.DefaultSnapshotLimit,
            CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task ListSnapshotsAsync_returns_null_when_server_does_not_exist(
        Guid serverId,
        [Frozen] Mock<IMonitoringReadRepository> repository,
        MonitoringReadService sut)
    {
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(false);

        var result = await sut.ListSnapshotsAsync(serverId, null, null, limit: 10, CancellationToken.None);

        result.Should().BeNull();
        repository.Verify(x => x.ServerExistsAsync(serverId, CancellationToken.None), Times.Once);
        repository.Verify(x => x.ListSnapshotsAsync(
            It.IsAny<Guid>(),
            It.IsAny<DateTimeOffset?>(),
            It.IsAny<DateTimeOffset?>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyNoOtherCalls();
    }
}
