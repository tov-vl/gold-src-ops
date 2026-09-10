using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlEndpointIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostServer_registers_server_through_migrated_postgresql_schema()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var request = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "postgres integration test");

        var response = await client.PostAsJsonAsync("/api/servers", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var server = await response.Content.ReadFromJsonAsync<ServerResponse>();
        server.Should().NotBeNull();
        var serverId = server!.Id;
        var persisted = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var entity = await dbContext.Servers
                .Include(x => x.CurrentState)
                .SingleAsync(x => x.Id == serverId);

            return new PersistedServer(
                entity.Id,
                entity.Name,
                entity.Game,
                entity.Endpoint.Host,
                entity.Endpoint.QueryPort,
                entity.Endpoint.RconPort,
                entity.PollIntervalSeconds,
                entity.Notes,
                entity.CurrentState?.Status,
                entity.CurrentState?.IsReachable,
                entity.CurrentState?.ConsecutiveFailures);
        });

        persisted.Should().BeEquivalentTo(new PersistedServer(
            serverId,
            "Dust2 Public",
            GameServerKind.GoldSrc,
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "postgres integration test",
            CurrentStatus: ServerStatus.Unknown,
            IsReachable: false,
            ConsecutiveFailures: 0));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_registration_with_same_idempotency_key_creates_one_paused_server()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var request = new RegisterServerRequest(
            "Concurrent registration",
            "game.example.test",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 60,
            Notes: null,
            IsEnabled: false);

        var firstTask = SendRegistrationAsync(firstClient, requestId, request);
        var secondTask = SendRegistrationAsync(secondClient, requestId, request);
        var responses = await Task.WhenAll(firstTask, secondTask);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        responses.Select(static response => response.StatusCode)
            .Should()
            .BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.OK]);
        var firstServer = await firstResponse.Content.ReadFromJsonAsync<ServerResponse>();
        var secondServer = await secondResponse.Content.ReadFromJsonAsync<ServerResponse>();
        firstServer.Should().NotBeNull();
        secondServer.Should().BeEquivalentTo(firstServer);
        firstServer!.IsEnabled.Should().BeFalse();

        var persisted = await factory.ExecuteDbContextAsync(async dbContext => new
        {
            Servers = await dbContext.Servers
                .CountAsync(server => server.RegistrationRequestId == requestId),
            States = await dbContext.ServerCurrentStates
                .CountAsync(state => state.ServerId == firstServer.Id)
        });
        persisted.Servers.Should().Be(1);
        persisted.States.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PatchServer_persists_editable_fields_through_postgresql_provider()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var createRequest = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "before");
        var createResponse = await client.PostAsJsonAsync("/api/servers", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        created.Should().NotBeNull();
        var updateRequest = new UpdateServerRequest(
            "Inferno Public",
            "cs.example.local",
            QueryPort: 27016,
            RconPort: 27017,
            PollIntervalSeconds: 45,
            Notes: "after");

        var response = await client.PatchAsJsonAsync($"/api/servers/{created!.Id}", updateRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persisted = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var entity = await dbContext.Servers
                .Include(x => x.CurrentState)
                .SingleAsync(x => x.Id == created.Id);

            return new PersistedServer(
                entity.Id,
                entity.Name,
                entity.Game,
                entity.Endpoint.Host,
                entity.Endpoint.QueryPort,
                entity.Endpoint.RconPort,
                entity.PollIntervalSeconds,
                entity.Notes,
                entity.CurrentState?.Status,
                entity.CurrentState?.IsReachable,
                entity.CurrentState?.ConsecutiveFailures);
        });

        persisted.Should().BeEquivalentTo(new PersistedServer(
            created.Id,
            "Inferno Public",
            GameServerKind.GoldSrc,
            "cs.example.local",
            QueryPort: 27016,
            RconPort: 27017,
            PollIntervalSeconds: 45,
            Notes: "after",
            CurrentStatus: ServerStatus.Unknown,
            IsReachable: false,
            ConsecutiveFailures: 0));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DisableServer_and_enableServer_persist_enabled_flag_through_postgresql_provider()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var createRequest = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: null);
        var createResponse = await client.PostAsJsonAsync("/api/servers", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        created.Should().NotBeNull();

        var disableResponse = await client.PostAsync($"/api/servers/{created!.Id}/disable", content: null);

        disableResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var disabled = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.Servers
                .Where(x => x.Id == created.Id)
                .Select(x => x.IsEnabled)
                .SingleAsync());
        disabled.Should().BeFalse();

        var enableResponse = await client.PostAsync($"/api/servers/{created.Id}/enable", content: null);

        enableResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var enabled = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.Servers
                .Where(x => x.Id == created.Id)
                .Select(x => x.IsEnabled)
                .SingleAsync());
        enabled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task GetServerSnapshots_filters_snapshots_using_postgresql_provider()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
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

        var response = await client.GetAsync(
            $"/api/servers/{seed.ServerId}/snapshots?from={ToQueryValue(fromUtc)}&to={ToQueryValue(toUtc)}&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await response.Content.ReadFromJsonAsync<SnapshotHistoryResponse>();
        history.Should().NotBeNull();
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
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task GetPublicA2sHistory_returns_sanitized_aggregate_for_enabled_servers()
    {
        var expectedToUtc = new DateTimeOffset(2026, 9, 7, 12, 30, 0, TimeSpan.Zero);
        // Seven ticks are below PostgreSQL's one-microsecond timestamp resolution.
        var now = expectedToUtc.AddTicks(7);
        var fromUtc = expectedToUtc.AddHours(-24);
        var clock = new TestClock(now);
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            },
            TestApiPrincipal.Anonymous);
        using var client = factory.CreateClient();
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var enabledServer = CreateServer(
                "Private enabled server sentinel",
                "enabled.private.example",
                createdAtUtc: fromUtc.AddDays(-1));
            var disabledServer = CreateServer(
                "Private disabled server sentinel",
                "disabled.private.example",
                createdAtUtc: fromUtc.AddDays(-1));
            disabledServer.Disable();

            dbContext.Servers.AddRange(enabledServer, disabledServer);
            dbContext.PollSnapshots.AddRange(
                PollSnapshot.Reachable(
                    enabledServer.Id,
                    fromUtc.AddMinutes(5),
                    18,
                    "private-map-sentinel",
                    7,
                    20,
                    0,
                    "private-version-sentinel"),
                PollSnapshot.Unreachable(
                    enabledServer.Id,
                    fromUtc.AddMinutes(35),
                    "private-failure-sentinel"),
                PollSnapshot.Reachable(
                    enabledServer.Id,
                    fromUtc.AddHours(1).AddMinutes(10),
                    19,
                    "private-map-sentinel",
                    8,
                    20,
                    0,
                    null),
                PollSnapshot.Unreachable(
                    disabledServer.Id,
                    fromUtc.AddHours(1).AddMinutes(15),
                    "disabled-failure-sentinel"),
                PollSnapshot.Reachable(
                    enabledServer.Id,
                    fromUtc.AddSeconds(-1),
                    17,
                    "before-window-sentinel",
                    0,
                    20,
                    0,
                    null),
                PollSnapshot.Unreachable(
                    enabledServer.Id,
                    now,
                    "exclusive-end-sentinel"));
            await dbContext.SaveChangesAsync();
        });

        var response = await client.GetAsync("/api/public/a2s-history?window=24h");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("sentinel");
        using var document = JsonDocument.Parse(payload);
        document.RootElement
            .EnumerateObject()
            .Select(static property => property.Name)
            .Should()
            .BeEquivalentTo(
                "window",
                "fromUtc",
                "toUtc",
                "bucketMinutes",
                "observedBuckets",
                "totalBuckets",
                "observedReachabilityPercent",
                "buckets");
        document.RootElement.GetProperty("buckets")[0]
            .EnumerateObject()
            .Select(static property => property.Name)
            .Should()
            .BeEquivalentTo("startedAtUtc", "state", "observedReachabilityPercent");

        var history = JsonSerializer.Deserialize<PublicA2sHistoryResponse>(payload, JsonSerializerOptions.Web);
        history.Should().NotBeNull();
        history!.Window.Should().Be("24h");
        history.FromUtc.Should().Be(fromUtc);
        history.ToUtc.Should().Be(expectedToUtc);
        history.BucketMinutes.Should().Be(60);
        history.ObservedBuckets.Should().Be(2);
        history.TotalBuckets.Should().Be(24);
        history.ObservedReachabilityPercent.Should().Be(66.7m);
        history.Buckets.Should().HaveCount(24);
        history.Buckets[0].Should().BeEquivalentTo(new PublicA2sBucketResponse(
            fromUtc,
            "degraded",
            50m));
        history.Buckets[1].Should().BeEquivalentTo(new PublicA2sBucketResponse(
            fromUtc.AddHours(1),
            "operational",
            100m));
        history.Buckets[2].Should().BeEquivalentTo(new PublicA2sBucketResponse(
            fromUtc.AddHours(2),
            "unknown",
            ObservedReachabilityPercent: null));
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

    private static async Task<HttpResponseMessage> SendRegistrationAsync(
        HttpClient client,
        Guid requestId,
        RegisterServerRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/servers")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", requestId.ToString("D"));

        return await client.SendAsync(message);
    }

    private static string ToQueryValue(DateTimeOffset value)
    {
        return Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));
    }

    private sealed record PersistedServer(
        Guid Id,
        string Name,
        GameServerKind Game,
        string Host,
        int QueryPort,
        int? RconPort,
        int PollIntervalSeconds,
        string? Notes,
        ServerStatus? CurrentStatus,
        bool? IsReachable,
        int? ConsecutiveFailures);

    private sealed record SnapshotSeed(Guid ServerId, Guid ExpectedSnapshotId);

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
