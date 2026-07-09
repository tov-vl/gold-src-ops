using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class ServerPollingServiceTests
{
    [Fact]
    public async Task PollDueServersAsync_opens_incident_after_failure_threshold()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero));
        var server = CreateServer(clock.UtcNow);
        var servers = new InMemoryServerRepository(server);
        var incidents = new InMemoryIncidentRepository();
        var queryClient = new StubGoldSrcServerQueryClient(_ => throw new TimeoutException("No response."));
        var sut = CreateService(servers, incidents, queryClient, clock);

        var result = await sut.PollDueServersAsync(CancellationToken.None);

        Assert.Equal(1, result.DueServers);
        Assert.Equal(1, result.FailedPolls);
        Assert.Equal(1, result.OpenedIncidents);
        Assert.Equal(ServerStatus.Offline, server.CurrentState?.Status);
        Assert.Equal(1, server.CurrentState?.ConsecutiveFailures);
        Assert.Single(servers.Snapshots);

        var incident = Assert.Single(incidents.Items);
        Assert.True(incident.IsOpen);
        Assert.Equal(server.Id, incident.ServerId);
        Assert.Equal(1, incident.ConsecutiveFailures);
    }

    [Fact]
    public async Task PollDueServersAsync_closes_open_incident_after_recovery()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero));
        var server = CreateServer(clock.UtcNow);
        var servers = new InMemoryServerRepository(server);
        var incidents = new InMemoryIncidentRepository();
        var queryClient = new StubGoldSrcServerQueryClient(_ => throw new TimeoutException("No response."));
        var sut = CreateService(servers, incidents, queryClient, clock);

        await sut.PollDueServersAsync(CancellationToken.None);

        queryClient.Query = endpoint => Task.FromResult(new GameServerInfo(
            ResponseFormat: "Source",
            Name: "CS 1.6 Test",
            Map: "de_dust2",
            Folder: "cstrike",
            Game: "Counter-Strike",
            Protocol: 48,
            Players: 10,
            MaxPlayers: 32,
            Bots: 0,
            ServerType: 'd',
            Environment: 'l',
            IsPrivate: false,
            HasVac: false,
            Version: "1.1.2.7/Stdio",
            Latency: TimeSpan.FromMilliseconds(42)));
        clock.Advance(TimeSpan.FromSeconds(server.PollIntervalSeconds + 1));

        var result = await sut.PollDueServersAsync(CancellationToken.None);

        Assert.Equal(1, result.SuccessfulPolls);
        Assert.Equal(1, result.ClosedIncidents);
        Assert.Equal(ServerStatus.Online, server.CurrentState?.Status);
        Assert.Equal(0, server.CurrentState?.ConsecutiveFailures);
        Assert.Equal("de_dust2", server.CurrentState?.CurrentMap);

        var incident = Assert.Single(incidents.Items);
        Assert.False(incident.IsOpen);
        Assert.NotNull(incident.ClosedAtUtc);
    }

    [Fact]
    public async Task PollDueServersAsync_skips_disabled_servers()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero));
        var server = CreateServer(clock.UtcNow);
        server.Disable();
        var servers = new InMemoryServerRepository(server);
        var incidents = new InMemoryIncidentRepository();
        var queryClient = new StubGoldSrcServerQueryClient(_ =>
            throw new InvalidOperationException("Disabled servers must not be queried."));
        var sut = CreateService(servers, incidents, queryClient, clock);

        var result = await sut.PollDueServersAsync(CancellationToken.None);

        Assert.Equal(0, result.DueServers);
        Assert.Equal(0, result.SuccessfulPolls);
        Assert.Equal(0, result.FailedPolls);
        Assert.Equal(0, result.OpenedIncidents);
        Assert.Equal(0, result.ClosedIncidents);
        Assert.Empty(servers.Snapshots);
        Assert.Empty(incidents.Items);
    }

    private static ServerPollingService CreateService(
        InMemoryServerRepository servers,
        InMemoryIncidentRepository incidents,
        StubGoldSrcServerQueryClient queryClient,
        TestClock clock)
    {
        return new ServerPollingService(
            servers,
            incidents,
            queryClient,
            clock,
            new ServerPollingSettings(
                QueryTimeout: TimeSpan.FromSeconds(1),
                BatchSize: 10,
                IncidentFailureThreshold: 1));
    }

    private static Server CreateServer(DateTimeOffset nowUtc) =>
        new(
            "CS 1.6 Test",
            GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", 27015, rconPort: null),
            pollIntervalSeconds: 1,
            notes: null,
            createdAtUtc: nowUtc);

    private sealed class TestClock : IClock
    {
        public TestClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan value)
        {
            UtcNow = UtcNow.Add(value);
        }
    }

    private sealed class StubGoldSrcServerQueryClient : IGoldSrcServerQueryClient
    {
        public StubGoldSrcServerQueryClient(Func<GameServerEndpoint, Task<GameServerInfo>> query)
        {
            Query = query;
        }

        public Func<GameServerEndpoint, Task<GameServerInfo>> Query { get; set; }

        public Task<GameServerInfo> QueryInfoAsync(
            GameServerEndpoint endpoint,
            CancellationToken cancellationToken) =>
            Query(endpoint);
    }

    private sealed class InMemoryServerRepository : IServerRepository
    {
        private readonly List<Server> _servers;

        public InMemoryServerRepository(params Server[] servers)
        {
            _servers = servers.ToList();
        }

        public List<PollSnapshot> Snapshots { get; } = [];

        public Task AddAsync(Server server, CancellationToken cancellationToken)
        {
            _servers.Add(server);
            return Task.CompletedTask;
        }

        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(_servers.FirstOrDefault(x => x.Id == id));
        }

        public Task<Server?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(_servers.FirstOrDefault(x => x.Id == id));
        }

        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Server>>(_servers);
        }

        public Task<IReadOnlyList<Server>> ListEnabledAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Server>>(_servers.Where(x => x.IsEnabled).ToArray());
        }

        public Task AddSnapshotAsync(PollSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryIncidentRepository : IIncidentRepository
    {
        public List<AvailabilityIncident> Items { get; } = [];

        public Task AddAsync(AvailabilityIncident incident, CancellationToken cancellationToken)
        {
            Items.Add(incident);
            return Task.CompletedTask;
        }

        public Task<AvailabilityIncident?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Items.FirstOrDefault(x => x.Id == id));
        }

        public Task<AvailabilityIncident?> GetOpenForServerAsync(
            Guid serverId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Items.FirstOrDefault(x => x.ServerId == serverId && x.IsOpen));
        }

        public Task<IReadOnlyList<AvailabilityIncident>> ListOpenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AvailabilityIncident>>(Items.Where(x => x.IsOpen).ToArray());
        }

        public Task<IReadOnlyList<AvailabilityIncident>> ListByServerAsync(
            Guid serverId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<AvailabilityIncident>>(
                Items.Where(x => x.ServerId == serverId).ToArray());
        }
    }
}
