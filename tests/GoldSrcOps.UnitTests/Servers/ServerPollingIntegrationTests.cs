using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Persistence;
using GoldSrcOps.UnitTests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class ServerPollingIntegrationTests
{
    [Fact]
    public async Task PollDueServersAsync_persists_online_state_and_reachable_snapshot_after_successful_query()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var queryClient = new FakeGoldSrcServerQueryClient();
        queryClient.EnqueueSuccess(CreateServerInfo());
        await using var factory = CreateFactory(clock, queryClient);
        var serverId = await SeedServerAsync(factory, clock.UtcNow);

        var result = await PollOnceAsync(factory);

        result.Should().BeEquivalentTo(new ServerPollingResult(
            DueServers: 1,
            SuccessfulPolls: 1,
            FailedPolls: 0,
            OpenedIncidents: 0,
            ClosedIncidents: 0));
        queryClient.QueryCount.Should().Be(1);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var state = await dbContext.ServerCurrentStates.SingleAsync(x => x.ServerId == serverId);
            state.Status.Should().Be(ServerStatus.Online);
            state.IsReachable.Should().BeTrue();
            state.LastCheckedAtUtc.Should().Be(clock.UtcNow);
            state.LastSuccessAtUtc.Should().Be(clock.UtcNow);
            state.LatencyMs.Should().Be(42);
            state.CurrentMap.Should().Be("de_dust2");
            state.Players.Should().Be(10);
            state.MaxPlayers.Should().Be(32);
            state.ConsecutiveFailures.Should().Be(0);

            var snapshot = await dbContext.PollSnapshots.SingleAsync(x => x.ServerId == serverId);
            snapshot.IsReachable.Should().BeTrue();
            snapshot.CheckedAtUtc.Should().Be(clock.UtcNow);
            snapshot.LatencyMs.Should().Be(42);
            snapshot.Map.Should().Be("de_dust2");
            snapshot.Players.Should().Be(10);
            snapshot.MaxPlayers.Should().Be(32);
            snapshot.Bots.Should().Be(0);
            snapshot.RawVersion.Should().Be("1.1.2.7/Stdio");
        });
    }

    [Fact]
    public async Task PollDueServersAsync_bounds_external_text_before_persistence()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var map = new string('m', PollSnapshot.MaxMapLength + 1);
        var rawVersion = new string('v', PollSnapshot.MaxRawVersionLength + 1);
        var queryClient = new FakeGoldSrcServerQueryClient();
        queryClient.EnqueueSuccess(CreateServerInfo(map, rawVersion));
        await using var factory = CreateFactory(clock, queryClient);
        var serverId = await SeedServerAsync(factory, clock.UtcNow);

        var result = await PollOnceAsync(factory);

        result.SuccessfulPolls.Should().Be(1);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var state = await dbContext.ServerCurrentStates.SingleAsync(x => x.ServerId == serverId);
            state.CurrentMap.Should().Be(map[..ServerCurrentState.MaxMapLength]);

            var snapshot = await dbContext.PollSnapshots.SingleAsync(x => x.ServerId == serverId);
            snapshot.Map.Should().Be(map[..PollSnapshot.MaxMapLength]);
            snapshot.RawVersion.Should().Be(rawVersion[..PollSnapshot.MaxRawVersionLength]);
        });
    }

    [Fact]
    public async Task PollDueServersAsync_bounds_failure_reason_before_persistence()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var failureReason = new string('f', PollSnapshot.MaxFailureReasonLength + 1);
        var queryClient = new FakeGoldSrcServerQueryClient();
        queryClient.EnqueueFailure(new InvalidOperationException(failureReason));
        await using var factory = CreateFactory(clock, queryClient);
        var serverId = await SeedServerAsync(factory, clock.UtcNow);

        var result = await PollOnceAsync(factory);

        result.FailedPolls.Should().Be(1);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var state = await dbContext.ServerCurrentStates.SingleAsync(x => x.ServerId == serverId);
            state.FailureReason.Should().Be(failureReason[..ServerCurrentState.MaxFailureReasonLength]);

            var snapshot = await dbContext.PollSnapshots.SingleAsync(x => x.ServerId == serverId);
            snapshot.FailureReason.Should().Be(failureReason[..PollSnapshot.MaxFailureReasonLength]);
        });
    }

    [Fact]
    public async Task PollDueServersAsync_opens_incident_after_repeated_failed_queries()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var queryClient = new FakeGoldSrcServerQueryClient();
        queryClient.EnqueueFailure(new TimeoutException("No response."));
        queryClient.EnqueueFailure(new TimeoutException("No response."));
        queryClient.EnqueueFailure(new TimeoutException("No response."));
        await using var factory = CreateFactory(clock, queryClient);
        var serverId = await SeedServerAsync(factory, clock.UtcNow);

        await PollOnceAsync(factory);
        clock.Advance(TimeSpan.FromSeconds(2));
        await PollOnceAsync(factory);
        clock.Advance(TimeSpan.FromSeconds(2));
        var result = await PollOnceAsync(factory);

        result.Should().BeEquivalentTo(new ServerPollingResult(
            DueServers: 1,
            SuccessfulPolls: 0,
            FailedPolls: 1,
            OpenedIncidents: 1,
            ClosedIncidents: 0));
        queryClient.QueryCount.Should().Be(3);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var state = await dbContext.ServerCurrentStates.SingleAsync(x => x.ServerId == serverId);
            state.Status.Should().Be(ServerStatus.Offline);
            state.IsReachable.Should().BeFalse();
            state.ConsecutiveFailures.Should().Be(3);
            state.FailureReason.Should().Be("No response.");

            var snapshots = await dbContext.PollSnapshots
                .Where(x => x.ServerId == serverId)
                .OrderBy(x => x.CheckedAtUtc)
                .ToListAsync();
            snapshots.Should().HaveCount(3);
            snapshots.Should().AllSatisfy(snapshot =>
            {
                snapshot.IsReachable.Should().BeFalse();
                snapshot.FailureReason.Should().Be("No response.");
            });

            var incident = await dbContext.AvailabilityIncidents.SingleAsync(x => x.ServerId == serverId);
            incident.IsOpen.Should().BeTrue();
            incident.ConsecutiveFailures.Should().Be(3);
            incident.StartReason.Should().Be("No response.");
        });
    }

    [Fact]
    public async Task PollDueServersAsync_skips_disabled_servers()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var queryClient = new FakeGoldSrcServerQueryClient();
        await using var factory = CreateFactory(clock, queryClient);
        var serverId = await SeedServerAsync(factory, clock.UtcNow, isEnabled: false);

        var result = await PollOnceAsync(factory);

        result.Should().BeEquivalentTo(new ServerPollingResult(
            DueServers: 0,
            SuccessfulPolls: 0,
            FailedPolls: 0,
            OpenedIncidents: 0,
            ClosedIncidents: 0));
        queryClient.QueryCount.Should().Be(0);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = await dbContext.Servers.SingleAsync(x => x.Id == serverId);
            server.IsEnabled.Should().BeFalse();
            var snapshots = await dbContext.PollSnapshots
                .Where(x => x.ServerId == serverId)
                .ToListAsync();
            snapshots.Should().BeEmpty();
        });
    }

    private static GoldSrcOpsApiFactory CreateFactory(
        TestClock clock,
        FakeGoldSrcServerQueryClient queryClient)
    {
        return new GoldSrcOpsApiFactory(services =>
        {
            services.RemoveAll<IClock>();
            services.RemoveAll<IGoldSrcServerQueryClient>();
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IGoldSrcServerQueryClient>(queryClient);
        });
    }

    private static async Task<Guid> SeedServerAsync(
        GoldSrcOpsApiFactory factory,
        DateTimeOffset createdAtUtc,
        bool isEnabled = true)
    {
        return await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = new Server(
                "Dust2 Public",
                GameServerKind.GoldSrc,
                new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
                pollIntervalSeconds: 1,
                notes: null,
                createdAtUtc);

            if (!isEnabled)
            {
                server.Disable();
            }

            dbContext.Servers.Add(server);
            await dbContext.SaveChangesAsync();

            return server.Id;
        });
    }

    private static async Task<ServerPollingResult> PollOnceAsync(GoldSrcOpsApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var polling = scope.ServiceProvider.GetRequiredService<ServerPollingService>();

        return await polling.PollDueServersAsync(CancellationToken.None);
    }

    private static GameServerInfo CreateServerInfo(
        string map = "de_dust2",
        string version = "1.1.2.7/Stdio")
    {
        return new GameServerInfo(
            ResponseFormat: "Source",
            Name: "CS 1.6 Test",
            Map: map,
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
            Version: version,
            Latency: TimeSpan.FromMilliseconds(42));
    }

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

    private sealed class FakeGoldSrcServerQueryClient : IGoldSrcServerQueryClient
    {
        private readonly Queue<Func<Task<GameServerInfo>>> _responses = [];

        public int QueryCount { get; private set; }

        public void EnqueueSuccess(GameServerInfo info)
        {
            _responses.Enqueue(() => Task.FromResult(info));
        }

        public void EnqueueFailure(Exception exception)
        {
            _responses.Enqueue(() => Task.FromException<GameServerInfo>(exception));
        }

        public Task<GameServerInfo> QueryInfoAsync(
            GameServerEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            QueryCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake query response was configured.");
            }

            return _responses.Dequeue()();
        }
    }
}
