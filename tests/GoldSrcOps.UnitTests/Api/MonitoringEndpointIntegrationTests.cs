using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Api;

public sealed class MonitoringEndpointIntegrationTests
{
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
}
