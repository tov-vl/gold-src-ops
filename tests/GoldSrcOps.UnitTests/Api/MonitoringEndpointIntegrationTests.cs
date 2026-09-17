using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.GameEvents;
using GoldSrcOps.Domain.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Api;

public sealed class MonitoringEndpointIntegrationTests
{
    private static readonly string[] ActivityRootProperties = ["limit", "items"];

    private static readonly string[] ActivityItemProperties =
    [
        "sourceId",
        "sourceType",
        "serverId",
        "serverName",
        "category",
        "state",
        "occurredAtUtc"
    ];

    private static readonly string[] FleetRootProperties = ["overview", "servers"];

    private static readonly string[] FleetOverviewProperties =
    [
        "totalServers",
        "enabledServers",
        "disabledServers",
        "onlineServers",
        "offlineServers",
        "unknownServers",
        "openIncidents",
        "lastCheckedAtUtc"
    ];

    private static readonly string[] FleetServerProperties =
    [
        "serverId",
        "name",
        "game",
        "host",
        "queryPort",
        "isEnabled",
        "pollIntervalSeconds",
        "status",
        "lastCheckedAtUtc",
        "latencyMs",
        "currentMap",
        "players",
        "maxPlayers",
        "bots",
        "consecutiveFailures",
        "openIncidents",
        "isStale",
        "requiresAttention"
    ];

    [Fact]
    public async Task GetServerSnapshots_returns_filtered_snapshots_in_reverse_chronological_order()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var fromUtc = new DateTimeOffset(2026, 4, 25, 9, 0, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        var seed = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = CreateServer("Dust2 Public", "127.0.0.1", createdAtUtc: fromUtc.AddHours(-1));
            var otherServer = CreateServer("Inferno Public", "127.0.0.2", createdAtUtc: fromUtc.AddHours(-1));
            var expectedSnapshot = PollSnapshot.Unreachable(server.Id, toUtc, "query timeout");

            dbContext.Servers.AddRange(server, otherServer);
            dbContext.PollSnapshots.AddRange(
                PollSnapshot.Reachable(server.Id, fromUtc.AddMinutes(-1), 18, "de_train", 8, 32, 0, "1.1.2.7/Stdio"),
                PollSnapshot.Reachable(server.Id, fromUtc.AddMinutes(30), 25, "de_dust2", 12, 32, 1, "1.1.2.7/Stdio"),
                expectedSnapshot,
                PollSnapshot.Reachable(otherServer.Id, toUtc.AddMinutes(1), 19, "de_inferno", 5, 24, 0, null));
            await dbContext.SaveChangesAsync();

            return new SnapshotSeed(server.Id, expectedSnapshot.Id);
        });
        var fromQuery = ToQueryValue(fromUtc);
        var toQuery = ToQueryValue(toUtc);

        var response = await client.GetAsync(
            $"/api/servers/{seed.ServerId}/snapshots?from={fromQuery}&to={toQuery}&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await response.Content.ReadFromJsonAsync<SnapshotHistoryResponse>();
        history.Should().NotBeNull();
        history.Should().BeEquivalentTo(new
        {
            seed.ServerId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Limit = 1
        });
        history!.Items.Should().ContainSingle();
        history.Items[0].Should().BeEquivalentTo(new
        {
            Id = seed.ExpectedSnapshotId,
            seed.ServerId,
            CheckedAtUtc = toUtc,
            IsReachable = false,
            LatencyMs = (int?)null,
            Map = (string?)null,
            Players = (int?)null,
            MaxPlayers = (int?)null,
            Bots = (int?)null,
            RawVersion = (string?)null,
            FailureReason = "query timeout"
        });
    }

    [Fact]
    public async Task GetServerSnapshots_returns_not_found_for_missing_server()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/servers/{Guid.NewGuid()}/snapshots");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetServerSnapshots_returns_validation_problem_for_invalid_date_range()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var fromUtc = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddMinutes(-1);

        var response = await client.GetAsync(
            $"/api/servers/{Guid.NewGuid()}/snapshots?from={ToQueryValue(fromUtc)}&to={ToQueryValue(toUtc)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetServerTrend_returns_not_found_for_missing_server()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/servers/{Guid.NewGuid()}/trends?window=1h");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2h")]
    [InlineData("30d")]
    public async Task GetServerTrend_rejects_an_unsupported_window(string window)
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/servers/{Guid.NewGuid()}/trends?window={Uri.EscapeDataString(window)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("window", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetServerIncidents_returns_requested_recent_limit_in_reverse_chronological_order()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var openedAtUtc = new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero);
        var seed = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = CreateServer("Incident fixture", "127.0.0.3", openedAtUtc.AddHours(-2));
            var olderIncident = AvailabilityIncident.Open(
                server.Id,
                openedAtUtc.AddHours(-1),
                "Earlier timeout",
                consecutiveFailures: 3);
            olderIncident.Close(openedAtUtc.AddMinutes(-30), "Probe recovered");
            var newestIncident = AvailabilityIncident.Open(
                server.Id,
                openedAtUtc,
                "Current timeout",
                consecutiveFailures: 4);

            dbContext.Servers.Add(server);
            dbContext.AvailabilityIncidents.AddRange(olderIncident, newestIncident);
            await dbContext.SaveChangesAsync();

            return new IncidentSeed(server.Id, newestIncident.Id);
        });

        var response = await client.GetAsync($"/api/servers/{seed.ServerId:D}/incidents?limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var incidents = await response.Content.ReadFromJsonAsync<AvailabilityIncidentResponse[]>();
        incidents.Should().ContainSingle();
        incidents![0].Should().BeEquivalentTo(new
        {
            Id = seed.ExpectedIncidentId,
            seed.ServerId,
            Type = "Unreachable",
            OpenedAtUtc = openedAtUtc,
            ClosedAtUtc = (DateTimeOffset?)null,
            StartReason = "Current timeout",
            EndReason = (string?)null,
            ConsecutiveFailures = 4
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(IncidentsService.MaxIncidentHistoryLimit + 1)]
    public async Task GetServerIncidents_returns_validation_problem_for_invalid_limit(int limit)
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/servers/{Guid.NewGuid():D}/incidents?limit={limit}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("limit", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetDashboardOverview_returns_aggregated_monitoring_state()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var now = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        var lastCheckedAtUtc = now.AddMinutes(-1);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var onlineServer = CreateServer("Dust2 Public", "127.0.0.1", createdAtUtc: now.AddHours(-1));
            onlineServer.GetCurrentState(now).MarkOnline(lastCheckedAtUtc, 20, "de_dust2", 14, 32);

            var offlineServer = CreateServer("Inferno Public", "127.0.0.2", createdAtUtc: now.AddHours(-1));
            offlineServer.GetCurrentState(now).MarkOffline(now.AddMinutes(-5), "query timeout");

            var unknownServer = CreateServer("Nuke Public", "127.0.0.3", createdAtUtc: now.AddMinutes(-30));

            dbContext.Servers.AddRange(onlineServer, offlineServer, unknownServer);
            dbContext.AvailabilityIncidents.Add(AvailabilityIncident.Open(
                offlineServer.Id,
                now.AddMinutes(-5),
                "query timeout",
                consecutiveFailures: 3));
            await dbContext.SaveChangesAsync();
        });

        var response = await client.GetAsync("/api/dashboard/overview");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var overview = await response.Content.ReadFromJsonAsync<DashboardOverviewResponse>();
        overview.Should().BeEquivalentTo(new DashboardOverviewResponse(
            TotalServers: 3,
            EnabledServers: 3,
            DisabledServers: 0,
            OnlineServers: 1,
            OfflineServers: 1,
            UnknownServers: 1,
            OpenIncidents: 1,
            LastCheckedAtUtc: lastCheckedAtUtc));
    }

    [Fact]
    public async Task GetDashboardFleet_returns_bounded_triage_projection()
    {
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        await using var factory = new GoldSrcOpsApiFactory(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(new TestClock(now));
        });
        using var client = factory.CreateClient();
        var serverIds = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var onlineServer = CreateServer("Dust2 Public", "127.0.0.1", now.AddHours(-1));
            onlineServer.GetCurrentState(now).MarkOnline(
                now.AddSeconds(-20),
                18,
                "de_dust2",
                4,
                20);
            var offlineServer = CreateServer("Inferno Public", "127.0.0.2", now.AddHours(-1));
            offlineServer.GetCurrentState(now).MarkOffline(now.AddSeconds(-30), "private failure detail");

            dbContext.Servers.AddRange(onlineServer, offlineServer);
            dbContext.PollSnapshots.AddRange(
                PollSnapshot.Reachable(
                    onlineServer.Id,
                    now.AddMinutes(-2),
                    20,
                    "de_train",
                    2,
                    20,
                    3,
                    "private version"),
                PollSnapshot.Reachable(
                    onlineServer.Id,
                    now.AddSeconds(-20),
                    18,
                    "de_dust2",
                    4,
                    20,
                    0,
                    "private version"),
                PollSnapshot.Unreachable(
                    offlineServer.Id,
                    now.AddSeconds(-30),
                    "private failure detail"));
            dbContext.AvailabilityIncidents.Add(AvailabilityIncident.Open(
                offlineServer.Id,
                now.AddSeconds(-30),
                "private incident detail",
                consecutiveFailures: 3));
            await dbContext.SaveChangesAsync();

            return (OnlineId: onlineServer.Id, OfflineId: offlineServer.Id);
        });

        var response = await client.GetAsync("/api/dashboard/fleet");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("private failure detail");
        payload.Should().NotContain("private incident detail");
        payload.Should().NotContain("private version");
        using var document = JsonDocument.Parse(payload);
        document.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo(FleetRootProperties);
        document.RootElement.GetProperty("overview").EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo(FleetOverviewProperties);
        document.RootElement.GetProperty("servers")[0].EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo(FleetServerProperties);
        var fleet = JsonSerializer.Deserialize<FleetOverviewResponse>(payload, JsonSerializerOptions.Web);
        fleet.Should().NotBeNull();
        fleet!.Overview.Should().BeEquivalentTo(new DashboardOverviewResponse(
            TotalServers: 2,
            EnabledServers: 2,
            DisabledServers: 0,
            OnlineServers: 1,
            OfflineServers: 1,
            UnknownServers: 0,
            OpenIncidents: 1,
            LastCheckedAtUtc: now.AddSeconds(-20)));
        fleet.Servers.Should().HaveCount(2);
        fleet.Servers[0].Should().BeEquivalentTo(new
        {
            ServerId = serverIds.OnlineId,
            Name = "Dust2 Public",
            Game = "GoldSrc",
            Host = "127.0.0.1",
            QueryPort = 27015,
            Status = "Online",
            Bots = (int?)0,
            OpenIncidents = 0,
            IsStale = false,
            RequiresAttention = false
        });
        fleet.Servers[1].Should().BeEquivalentTo(new
        {
            ServerId = serverIds.OfflineId,
            Name = "Inferno Public",
            Status = "Offline",
            Bots = (int?)null,
            OpenIncidents = 1,
            IsStale = false,
            RequiresAttention = true
        });
    }

    [Fact]
    public async Task GetDashboardActivity_returns_a_bounded_sanitized_cross_source_timeline()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        var now = new DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero);
        var seed = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = CreateServer("Activity fixture", "private.example.test", now.AddDays(-1));
            var recoveredIncident = AvailabilityIncident.Open(
                server.Id,
                now.AddMinutes(-20),
                "private incident start reason",
                consecutiveFailures: 3);
            recoveredIncident.Close(now.AddMinutes(-3), "private incident recovery reason");
            var openIncident = AvailabilityIncident.Open(
                server.Id,
                now.AddMinutes(-2),
                "private current incident reason",
                consecutiveFailures: 4);
            var latestCommand = new CommandExecution(
                server.Id,
                ServerCommandType.Say,
                "private command payload",
                "private requester",
                now.AddMinutes(-1.5));
            latestCommand.MarkRunning(now.AddMinutes(-1.25));
            latestCommand.MarkSucceeded(now.AddMinutes(-1), "private command result");
            var excludedCommand = new CommandExecution(
                server.Id,
                ServerCommandType.Restart,
                payload: null,
                "private requester",
                now.AddMinutes(-5));
            excludedCommand.MarkFailed(now.AddMinutes(-4), "private command failure");
            var gameplayEvent = new GameEventInboxEntry(
                Guid.NewGuid(),
                server.Id,
                Guid.NewGuid(),
                sequenceNumber: 1,
                GameEventInboxEntry.CurrentContractVersion,
                GameEventType.RoundEnded,
                now.AddSeconds(-30),
                now.AddSeconds(-25),
                "private_activity_map",
                players: 12,
                bots: 0,
                new string('C', GameEventInboxEntry.MaxIntentHashLength));

            dbContext.Servers.Add(server);
            dbContext.AvailabilityIncidents.AddRange(recoveredIncident, openIncident);
            dbContext.CommandExecutions.AddRange(latestCommand, excludedCommand);
            dbContext.GameEventInbox.Add(gameplayEvent);
            await dbContext.SaveChangesAsync();

            return new
            {
                server.Id,
                GameplayEventId = gameplayEvent.Id,
                LatestCommandId = latestCommand.Id,
                OpenIncidentId = openIncident.Id,
                RecoveredIncidentId = recoveredIncident.Id,
                ExcludedCommandId = excludedCommand.Id
            };
        });

        var response = await client.GetAsync("/api/dashboard/activity?limit=4");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("private.example.test");
        payload.Should().NotContain("private incident");
        payload.Should().NotContain("private command");
        payload.Should().NotContain("private requester");
        payload.Should().NotContain("private_activity_map");
        payload.Should().NotContain(new string('C', GameEventInboxEntry.MaxIntentHashLength));
        payload.Should().NotContain(seed.ExcludedCommandId.ToString());
        using var document = JsonDocument.Parse(payload);
        document.RootElement.EnumerateObject().Select(static property => property.Name)
            .Should().BeEquivalentTo(ActivityRootProperties);
        document.RootElement.GetProperty("items")[0].EnumerateObject()
            .Select(static property => property.Name)
            .Should().BeEquivalentTo(ActivityItemProperties);
        var activity = JsonSerializer.Deserialize<OperationsActivityResponse>(payload, JsonSerializerOptions.Web);
        activity.Should().NotBeNull();
        activity!.Limit.Should().Be(4);
        activity.Items.Should().HaveCount(4);
        activity.Items.Select(static item => item.SourceId).Should().ContainInOrder(
            seed.GameplayEventId,
            seed.LatestCommandId,
            seed.OpenIncidentId,
            seed.RecoveredIncidentId);
        activity.Items[0].Should().BeEquivalentTo(new
        {
            SourceId = seed.GameplayEventId,
            SourceType = "Gameplay",
            ServerId = seed.Id,
            ServerName = "Activity fixture",
            Category = "round.ended",
            State = "Recorded",
            OccurredAtUtc = now.AddSeconds(-30)
        });
        activity.Items[1].Should().BeEquivalentTo(new
        {
            SourceId = seed.LatestCommandId,
            SourceType = "Command",
            ServerId = seed.Id,
            ServerName = "Activity fixture",
            Category = "Say",
            State = "Succeeded",
            OccurredAtUtc = now.AddMinutes(-1)
        });
        activity.Items[2].Should().BeEquivalentTo(new
        {
            SourceId = seed.OpenIncidentId,
            SourceType = "Incident",
            State = "Open",
            OccurredAtUtc = now.AddMinutes(-2)
        });
        activity.Items[3].Should().BeEquivalentTo(new
        {
            SourceId = seed.RecoveredIncidentId,
            SourceType = "Incident",
            State = "Recovered",
            OccurredAtUtc = now.AddMinutes(-3)
        });
    }

    [Fact]
    public async Task GetDashboardActivity_applies_server_and_kind_before_the_limit()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        var now = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var seed = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var targetServer = CreateServer("Target activity server", "target.example.test", now.AddDays(-1));
            var otherServer = CreateServer("Other activity server", "other.example.test", now.AddDays(-1));
            var newerTargetCommand = new CommandExecution(
                targetServer.Id,
                ServerCommandType.Say,
                "private command payload",
                "private requester",
                now.AddMinutes(-1));
            var targetGameplayEvent = new GameEventInboxEntry(
                Guid.NewGuid(),
                targetServer.Id,
                Guid.NewGuid(),
                sequenceNumber: 1,
                GameEventInboxEntry.CurrentContractVersion,
                GameEventType.RoundEnded,
                now.AddMinutes(-10),
                now.AddMinutes(-9),
                "private_target_map",
                players: 8,
                bots: 0,
                new string('D', GameEventInboxEntry.MaxIntentHashLength));
            var newerOtherGameplayEvent = new GameEventInboxEntry(
                Guid.NewGuid(),
                otherServer.Id,
                Guid.NewGuid(),
                sequenceNumber: 1,
                GameEventInboxEntry.CurrentContractVersion,
                GameEventType.RoundEnded,
                now.AddSeconds(-30),
                now.AddSeconds(-25),
                "private_other_map",
                players: 4,
                bots: 0,
                new string('E', GameEventInboxEntry.MaxIntentHashLength));

            dbContext.Servers.AddRange(targetServer, otherServer);
            dbContext.CommandExecutions.Add(newerTargetCommand);
            dbContext.GameEventInbox.AddRange(targetGameplayEvent, newerOtherGameplayEvent);
            await dbContext.SaveChangesAsync();

            return new
            {
                TargetServerId = targetServer.Id,
                TargetGameplayEventId = targetGameplayEvent.Id
            };
        });

        var response = await client.GetAsync(
            $"/api/dashboard/activity?limit=1&serverId={seed.TargetServerId:D}&kind=gameplay");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var activity = await response.Content.ReadFromJsonAsync<OperationsActivityResponse>();
        activity.Should().NotBeNull();
        activity!.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            SourceId = seed.TargetGameplayEventId,
            SourceType = "Gameplay",
            ServerId = seed.TargetServerId,
            ServerName = "Target activity server",
            Category = "round.ended",
            State = "Recorded",
            OccurredAtUtc = now.AddMinutes(-10)
        });
    }

    [Fact]
    public async Task GetDashboardActivity_returns_validation_problem_for_invalid_kind()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/dashboard/activity?kind=raw-rcon");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("kind", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MonitoringReadService.MaxActivityLimit + 1)]
    public async Task GetDashboardActivity_returns_validation_problem_for_invalid_limit(int limit)
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/dashboard/activity?limit={limit}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("limit", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetPublicStatus_returns_sanitized_enabled_fleet_summary_for_anonymous_client()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Anonymous);
        using var client = factory.CreateClient();
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var lastObservedAtUtc = now.AddMinutes(-1);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var onlineServer = CreateServer("Dust2 Public", "127.0.0.1", createdAtUtc: now.AddHours(-1));
            onlineServer.GetCurrentState(now).MarkOnline(lastObservedAtUtc, 20, "de_dust2", 14, 32);

            var offlineServer = CreateServer("Inferno Public", "127.0.0.2", createdAtUtc: now.AddHours(-1));
            offlineServer.GetCurrentState(now).MarkOffline(now.AddMinutes(-5), "query timeout");

            var disabledServer = CreateServer("Nuke Public", "127.0.0.3", createdAtUtc: now.AddHours(-1));
            disabledServer.GetCurrentState(now).MarkOffline(now, "maintenance");
            disabledServer.Disable();

            dbContext.Servers.AddRange(onlineServer, offlineServer, disabledServer);
            dbContext.AvailabilityIncidents.AddRange(
                AvailabilityIncident.Open(
                    offlineServer.Id,
                    now.AddMinutes(-5),
                    "query timeout",
                    consecutiveFailures: 3),
                AvailabilityIncident.Open(
                    disabledServer.Id,
                    now,
                    "maintenance",
                    consecutiveFailures: 3));
            await dbContext.SaveChangesAsync();
        });

        var response = await client.GetAsync("/api/public/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        document.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .Should()
            .BeEquivalentTo(
                "state",
                "monitoredServers",
                "onlineServers",
                "serversRequiringAttention",
                "openIncidents",
                "lastObservedAtUtc");

        var status = JsonSerializer.Deserialize<PublicStatusResponse>(
            payload,
            JsonSerializerOptions.Web);
        status.Should().BeEquivalentTo(new PublicStatusResponse(
            State: "degraded",
            MonitoredServers: 2,
            OnlineServers: 1,
            ServersRequiringAttention: 1,
            OpenIncidents: 1,
            LastObservedAtUtc: lastObservedAtUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1h")]
    [InlineData("30d")]
    public async Task GetPublicA2sHistory_rejects_an_unsupported_window(string window)
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Anonymous);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/public/a2s-history?window={Uri.EscapeDataString(window)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("window", out _).Should().BeTrue();
    }

    private static Server CreateServer(string name, string host, DateTimeOffset createdAtUtc)
    {
        return new Server(
            name,
            GameServerKind.GoldSrc,
            new ServerEndpoint(host, queryPort: 27015, rconPort: null),
            pollIntervalSeconds: 30,
            notes: null,
            createdAtUtc);
    }

    private static string ToQueryValue(DateTimeOffset value)
    {
        return Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));
    }

    private sealed record SnapshotSeed(Guid ServerId, Guid ExpectedSnapshotId);

    private sealed record IncidentSeed(Guid ServerId, Guid ExpectedIncidentId);

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
