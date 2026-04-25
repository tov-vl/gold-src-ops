using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class MonitoringReadServiceTests
{
    [Fact]
    public async Task GetDashboardOverviewAsync_counts_server_statuses_and_open_incidents()
    {
        var now = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryMonitoringReadRepository
        {
            OpenIncidentCount = 2,
            DashboardServerStatuses =
            [
                new DashboardServerStatusDto(Guid.NewGuid(), IsEnabled: true, ServerStatus.Online, now.AddMinutes(-5)),
                new DashboardServerStatusDto(Guid.NewGuid(), IsEnabled: true, ServerStatus.Offline, now),
                new DashboardServerStatusDto(Guid.NewGuid(), IsEnabled: false, ServerStatus.Unknown, null)
            ]
        };
        var sut = new MonitoringReadService(repository);

        var result = await sut.GetDashboardOverviewAsync(CancellationToken.None);

        Assert.Equal(3, result.TotalServers);
        Assert.Equal(2, result.EnabledServers);
        Assert.Equal(1, result.DisabledServers);
        Assert.Equal(1, result.OnlineServers);
        Assert.Equal(1, result.OfflineServers);
        Assert.Equal(1, result.UnknownServers);
        Assert.Equal(2, result.OpenIncidents);
        Assert.Equal(now, result.LastCheckedAtUtc);
    }

    [Fact]
    public async Task ListSnapshotsAsync_uses_default_limit_for_recent_history()
    {
        var serverId = Guid.NewGuid();
        var fromUtc = new DateTimeOffset(2026, 4, 25, 9, 0, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        var snapshot = new PollSnapshotDto(
            Guid.NewGuid(),
            serverId,
            toUtc,
            IsReachable: true,
            LatencyMs: 42,
            Map: "de_dust2",
            Players: 10,
            MaxPlayers: 32,
            Bots: 0,
            RawVersion: "1.1.2.7/Stdio",
            FailureReason: null);
        var repository = new InMemoryMonitoringReadRepository
        {
            ExistingServerIds = [serverId],
            Snapshots = [snapshot]
        };
        var sut = new MonitoringReadService(repository);

        var result = await sut.ListSnapshotsAsync(serverId, fromUtc, toUtc, limit: null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(serverId, result.ServerId);
        Assert.Equal(fromUtc, result.FromUtc);
        Assert.Equal(toUtc, result.ToUtc);
        Assert.Equal(MonitoringReadService.DefaultSnapshotLimit, result.Limit);
        Assert.Equal(MonitoringReadService.DefaultSnapshotLimit, repository.CapturedSnapshotLimit);
        Assert.Equal(fromUtc, repository.CapturedSnapshotFromUtc);
        Assert.Equal(toUtc, repository.CapturedSnapshotToUtc);
        Assert.Same(snapshot, Assert.Single(result.Items));
    }

    [Fact]
    public async Task ListSnapshotsAsync_returns_null_when_server_does_not_exist()
    {
        var repository = new InMemoryMonitoringReadRepository();
        var sut = new MonitoringReadService(repository);

        var result = await sut.ListSnapshotsAsync(Guid.NewGuid(), null, null, limit: 10, CancellationToken.None);

        Assert.Null(result);
        Assert.False(repository.SnapshotsWereQueried);
    }

    private sealed class InMemoryMonitoringReadRepository : IMonitoringReadRepository
    {
        public HashSet<Guid> ExistingServerIds { get; init; } = [];

        public IReadOnlyList<PollSnapshotDto> Snapshots { get; init; } = [];

        public IReadOnlyList<DashboardServerStatusDto> DashboardServerStatuses { get; init; } = [];

        public int OpenIncidentCount { get; init; }

        public bool SnapshotsWereQueried { get; private set; }

        public DateTimeOffset? CapturedSnapshotFromUtc { get; private set; }

        public DateTimeOffset? CapturedSnapshotToUtc { get; private set; }

        public int? CapturedSnapshotLimit { get; private set; }

        public Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ExistingServerIds.Contains(serverId));
        }

        public Task<IReadOnlyList<PollSnapshotDto>> ListSnapshotsAsync(
            Guid serverId,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int limit,
            CancellationToken cancellationToken)
        {
            SnapshotsWereQueried = true;
            CapturedSnapshotFromUtc = fromUtc;
            CapturedSnapshotToUtc = toUtc;
            CapturedSnapshotLimit = limit;

            return Task.FromResult(Snapshots);
        }

        public Task<IReadOnlyList<DashboardServerStatusDto>> ListDashboardServerStatusesAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult(DashboardServerStatuses);
        }

        public Task<int> CountOpenIncidentsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(OpenIncidentCount);
        }
    }
}
