using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class MonitoringReadServiceTests
{
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
