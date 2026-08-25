using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.Infrastructure.Persistence;
using GoldSrcOps.Infrastructure.Persistence.Outbox;
using GoldSrcOps.UnitTests.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class PostgreSqlServerPollingOutboxIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PollDueServersAsync_commits_ordered_alerts_without_duplicate_unavailable_event()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        var queryClient = new FakeGoldSrcServerQueryClient();
        queryClient.EnqueueFailure(new TimeoutException("No response."));
        queryClient.EnqueueFailure(new TimeoutException("No response."));
        queryClient.EnqueueSuccess(CreateServerInfo());
        await using var factory = await CreateFactoryAsync(clock, queryClient);
        var serverId = await SeedServerAsync(factory, clock.UtcNow);

        var unavailableResult = await PollOnceAsync(factory);
        clock.Advance(TimeSpan.FromSeconds(2));
        var repeatedFailureResult = await PollOnceAsync(factory);

        unavailableResult.OpenedIncidents.Should().Be(1);
        repeatedFailureResult.OpenedIncidents.Should().Be(0);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var messages = await dbContext.OutboxMessages.AsNoTracking().ToListAsync();
            messages.Should().ContainSingle();

            var incident = await dbContext.AvailabilityIncidents
                .AsNoTracking()
                .SingleAsync(x => x.ServerId == serverId);
            incident.IsOpen.Should().BeTrue();
            incident.ConsecutiveFailures.Should().Be(2);

            var alert = DeserializeAlert(messages[0]);
            AssertUnavailableAlert(messages[0], alert, incident, serverId, clock.UtcNow.AddSeconds(-2));
        });

        clock.Advance(TimeSpan.FromSeconds(2));
        var recoveredResult = await PollOnceAsync(factory);

        recoveredResult.ClosedIncidents.Should().Be(1);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var incident = await dbContext.AvailabilityIncidents
                .AsNoTracking()
                .SingleAsync(x => x.ServerId == serverId);
            var messages = await dbContext.OutboxMessages
                .AsNoTracking()
                .OrderBy(x => x.OccurredAtUtc)
                .ToListAsync();

            incident.IsOpen.Should().BeFalse();
            incident.ClosedAtUtc.Should().Be(clock.UtcNow);
            incident.ConsecutiveFailures.Should().Be(2);
            messages.Should().HaveCount(2);

            var unavailable = DeserializeAlert(messages[0]);
            AssertUnavailableAlert(messages[0], unavailable, incident, serverId, clock.UtcNow.AddSeconds(-4));

            var recovered = DeserializeAlert(messages[1]);
            messages[1].Id.Should().Be(recovered.EventId);
            messages[1].EventType.Should().Be(IncidentAlertEvents.ServerRecovered);
            messages[1].AggregateType.Should().Be(IncidentAlertEvents.AggregateType);
            messages[1].AggregateId.Should().Be(incident.Id);
            messages[1].Status.Should().Be(OutboxMessageStatus.Pending);
            recovered.EventType.Should().Be(IncidentAlertEvents.ServerRecovered);
            recovered.OccurredAtUtc.Should().Be(clock.UtcNow);
            recovered.IncidentId.Should().Be(incident.Id);
            recovered.ServerId.Should().Be(serverId);
            recovered.ServerName.Should().Be("Dust2 Public");
            recovered.Reason.Should().Be("Server query recovered.");
            recovered.ConsecutiveFailures.Should().Be(2);
            recovered.OpenedAtUtc.Should().Be(clock.UtcNow.AddSeconds(-4));
            recovered.ClosedAtUtc.Should().Be(clock.UtcNow);
            recovered.DurationSeconds.Should().Be(4);
        });
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PollDueServersAsync_rolls_back_incident_state_and_outbox_when_commit_fails()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        var queryClient = new FakeGoldSrcServerQueryClient();
        queryClient.EnqueueFailure(new TimeoutException("No response."));
        await using var factory = await CreateFactoryAsync(
            clock,
            queryClient,
            services =>
            {
                services.RemoveAll<IOutboxWriter>();
                services.AddScoped<IOutboxWriter, DuplicateOutboxWriter>();
            });
        var serverId = await SeedServerAsync(factory, clock.UtcNow);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => PollOnceAsync(factory));

        var databaseException = exception.InnerException.Should().BeOfType<PostgresException>().Subject;
        databaseException.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        databaseException.ConstraintName.Should().Be("UX_outbox_messages_EventType_AggregateId");

        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            (await dbContext.Servers.CountAsync(x => x.Id == serverId)).Should().Be(1);
            var state = await dbContext.ServerCurrentStates.SingleAsync(x => x.ServerId == serverId);
            state.Status.Should().Be(ServerStatus.Unknown);
            state.LastCheckedAtUtc.Should().Be(clock.UtcNow);
            state.FailureReason.Should().BeNull();
            state.ConsecutiveFailures.Should().Be(0);
            (await dbContext.PollSnapshots.CountAsync(x => x.ServerId == serverId)).Should().Be(0);
            (await dbContext.AvailabilityIncidents.CountAsync(x => x.ServerId == serverId)).Should().Be(0);
            (await dbContext.OutboxMessages.CountAsync()).Should().Be(0);
        });
    }

    private static async Task<PostgreSqlGoldSrcOpsApiFactory> CreateFactoryAsync(
        TestClock clock,
        FakeGoldSrcServerQueryClient queryClient,
        Action<IServiceCollection>? configureServices = null)
    {
        return await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(services =>
        {
            services.RemoveAll<IClock>();
            services.RemoveAll<IGoldSrcServerQueryClient>();
            services.RemoveAll<ServerPollingSettings>();
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IGoldSrcServerQueryClient>(queryClient);
            services.AddSingleton(new ServerPollingSettings(
                QueryTimeout: TimeSpan.FromSeconds(1),
                BatchSize: 10,
                IncidentFailureThreshold: 1));
            configureServices?.Invoke(services);
        });
    }

    private static async Task<Guid> SeedServerAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        DateTimeOffset createdAtUtc)
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

            dbContext.Servers.Add(server);
            await dbContext.SaveChangesAsync();
            return server.Id;
        });
    }

    private static async Task<ServerPollingResult> PollOnceAsync(
        PostgreSqlGoldSrcOpsApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var polling = scope.ServiceProvider.GetRequiredService<ServerPollingService>();
        return await polling.PollDueServersAsync(CancellationToken.None);
    }

    private static IncidentAlertEventV1 DeserializeAlert(OutboxMessage message)
    {
        return JsonSerializer.Deserialize<IncidentAlertEventV1>(
                message.Payload,
                JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("The outbox payload is not a valid incident alert event.");
    }

    private static void AssertUnavailableAlert(
        OutboxMessage message,
        IncidentAlertEventV1 alert,
        AvailabilityIncident incident,
        Guid serverId,
        DateTimeOffset openedAtUtc)
    {
        message.Id.Should().Be(alert.EventId);
        message.EventType.Should().Be(IncidentAlertEvents.ServerUnavailable);
        message.AggregateType.Should().Be(IncidentAlertEvents.AggregateType);
        message.AggregateId.Should().Be(incident.Id);
        message.Status.Should().Be(OutboxMessageStatus.Pending);
        message.AttemptCount.Should().Be(0);
        alert.EventType.Should().Be(IncidentAlertEvents.ServerUnavailable);
        alert.PayloadVersion.Should().Be(IncidentAlertEventV1.CurrentPayloadVersion);
        alert.OccurredAtUtc.Should().Be(openedAtUtc);
        alert.IncidentId.Should().Be(incident.Id);
        alert.ServerId.Should().Be(serverId);
        alert.ServerName.Should().Be("Dust2 Public");
        alert.Reason.Should().Be("No response.");
        alert.ConsecutiveFailures.Should().Be(1);
        alert.OpenedAtUtc.Should().Be(openedAtUtc);
        alert.ClosedAtUtc.Should().BeNull();
        alert.DurationSeconds.Should().BeNull();
    }

    private static GameServerInfo CreateServerInfo() =>
        new(
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
            Latency: TimeSpan.FromMilliseconds(42));

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
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake query response was configured.");
            }

            return _responses.Dequeue()();
        }
    }

    private sealed class DuplicateOutboxWriter(GoldSrcOpsDbContext dbContext) : IOutboxWriter
    {
        public void Add(IncidentAlertEventV1 alert)
        {
            dbContext.OutboxMessages.Add(CreateMessage(alert));
            dbContext.OutboxMessages.Add(CreateMessage(alert));
        }

        private static OutboxMessage CreateMessage(IncidentAlertEventV1 alert) =>
            new(
                Guid.NewGuid(),
                alert.EventType,
                alert.PayloadVersion,
                IncidentAlertEvents.AggregateType,
                alert.IncidentId,
                alert.OccurredAtUtc,
                "{}");
    }
}
