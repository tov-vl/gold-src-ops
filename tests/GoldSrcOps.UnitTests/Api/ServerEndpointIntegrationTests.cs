using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.UnitTests.Api;

public sealed class ServerEndpointIntegrationTests
{
    [Fact]
    public async Task PostServer_registers_server_and_returns_created_contract()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var request = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "integration test");

        var response = await client.PostAsJsonAsync("/api/servers", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var server = await response.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(server);
        Assert.NotEqual(Guid.Empty, server.Id);
        Assert.Equal(1, server.Revision);
        Assert.Equal("Dust2 Public", server.Name);
        Assert.Equal("GoldSrc", server.Game);
        Assert.Equal("127.0.0.1", server.Host);
        Assert.Equal(27015, server.QueryPort);
        Assert.Null(server.RconPort);
        Assert.True(server.IsEnabled);
        Assert.Equal(30, server.PollIntervalSeconds);
        Assert.Equal("integration test", server.Notes);
    }

    [Fact]
    public async Task PostServer_can_register_paused_server_with_idempotency_key()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var request = new RegisterServerRequest(
            "Paused server",
            "game.example.test",
            QueryPort: 27015,
            RconPort: 27016,
            PollIntervalSeconds: 60,
            Notes: "Awaiting operator activation",
            IsEnabled: false);

        using var response = await SendRegistrationAsync(client, requestId, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var server = await response.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(server);
        Assert.False(server.IsEnabled);
        Assert.Equal(27016, server.RconPort);

        var registrationMetadata = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.Servers
                .Where(candidate => candidate.Id == server.Id)
                .Select(candidate => new
                {
                    candidate.RegistrationRequestId,
                    candidate.RegistrationIntentHash
                })
                .SingleAsync());
        Assert.Equal(requestId, registrationMetadata.RegistrationRequestId);
        Assert.Equal(64, registrationMetadata.RegistrationIntentHash?.Length);
    }

    [Fact]
    public async Task PostServer_reuses_same_registration_for_same_idempotency_key_and_intent()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var request = new RegisterServerRequest(
            "Idempotent server",
            "game.example.test",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 60,
            Notes: null,
            IsEnabled: false);

        using var firstResponse = await SendRegistrationAsync(client, requestId, request);
        using var secondResponse = await SendRegistrationAsync(
            client,
            requestId,
            request with { Host = "GAME.EXAMPLE.TEST" });

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<ServerResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(first);
        Assert.Equal(first, second);

        var serverCount = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.Servers.CountAsync());
        Assert.Equal(1, serverCount);
    }

    [Fact]
    public async Task PostServer_rejects_reused_idempotency_key_for_different_intent()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var requestId = Guid.NewGuid();
        var request = new RegisterServerRequest(
            "First server",
            "game.example.test",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 60,
            Notes: null,
            IsEnabled: false);

        using var firstResponse = await SendRegistrationAsync(client, requestId, request);
        using var conflictResponse = await SendRegistrationAsync(
            client,
            requestId,
            request with { Name = "Different server" });

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        var body = await conflictResponse.Content.ReadAsStringAsync();
        Assert.Contains("server_registration.idempotency_conflict", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostServer_rejects_malformed_idempotency_key()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var request = new RegisterServerRequest(
            "Invalid key server",
            "game.example.test",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 60,
            Notes: null);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/servers")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", "not-a-uuid");

        using var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetServerStatus_returns_unknown_status_after_registration()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var request = new RegisterServerRequest(
            "Status Test",
            "localhost",
            QueryPort: 27015,
            RconPort: 27015,
            PollIntervalSeconds: null,
            Notes: null);

        var createResponse = await client.PostAsJsonAsync("/api/servers", request);
        createResponse.EnsureSuccessStatusCode();
        var server = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(server);

        var statusResponse = await client.GetAsync($"/api/servers/{server.Id}/status");

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<ServerStatusResponse>();
        Assert.NotNull(status);
        Assert.Equal(server.Id, status.ServerId);
        Assert.Equal("Unknown", status.Status);
        Assert.False(status.IsReachable);
        Assert.Null(status.LastSuccessAtUtc);
        Assert.Null(status.LatencyMs);
        Assert.Null(status.CurrentMap);
        Assert.Null(status.Players);
        Assert.Null(status.MaxPlayers);
        Assert.Null(status.FailureReason);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task PatchServer_updates_server_and_returns_updated_contract()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var createRequest = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "before",
            IsEnabled: false);
        var createResponse = await client.PostAsJsonAsync("/api/servers", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(created);
        var updateRequest = new UpdateServerRequest(
            created.Revision,
            "Inferno Public",
            "cs.example.local",
            QueryPort: 27016,
            RconPort: 27017,
            PollIntervalSeconds: 45,
            Notes: "after");

        var response = await client.PatchAsJsonAsync($"/api/servers/{created.Id}", updateRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(updated);
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.Revision + 1, updated.Revision);
        Assert.Equal("Inferno Public", updated.Name);
        Assert.Equal("GoldSrc", updated.Game);
        Assert.Equal("cs.example.local", updated.Host);
        Assert.Equal(27016, updated.QueryPort);
        Assert.Equal(27017, updated.RconPort);
        Assert.False(updated.IsEnabled);
        Assert.Equal(45, updated.PollIntervalSeconds);
        Assert.Equal("after", updated.Notes);
        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);

        var getResponse = await client.GetAsync($"/api/servers/{created.Id}");
        getResponse.EnsureSuccessStatusCode();
        var persisted = await getResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(persisted);
        Assert.Equal(updated, persisted);
    }

    [Fact]
    public async Task PatchServer_returns_not_found_for_missing_server()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var request = new UpdateServerRequest(
            ExpectedRevision: 1,
            "Missing Server",
            "localhost",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: null);

        var response = await client.PatchAsJsonAsync($"/api/servers/{Guid.NewGuid()}", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchServer_returns_validation_problem_for_invalid_request()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var request = new UpdateServerRequest(
            ExpectedRevision: 0,
            " ",
            "",
            QueryPort: 0,
            RconPort: 70000,
            PollIntervalSeconds: 0,
            Notes: null);

        var response = await client.PatchAsJsonAsync($"/api/servers/{Guid.NewGuid()}", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchServer_rejects_updates_while_monitoring_is_enabled()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync(
            "/api/servers",
            new RegisterServerRequest(
                "Enabled server",
                "game.example.test",
                QueryPort: 27015,
                RconPort: null,
                PollIntervalSeconds: 30,
                Notes: null));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(created);

        var response = await client.PatchAsJsonAsync(
            $"/api/servers/{created.Id}",
            new UpdateServerRequest(
                created.Revision,
                "Edited server",
                created.Host,
                created.QueryPort,
                created.RconPort,
                created.PollIntervalSeconds,
                created.Notes));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("server_update.monitoring_must_be_paused", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchServer_rejects_a_stale_configuration_revision()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync(
            "/api/servers",
            new RegisterServerRequest(
                "Paused server",
                "game.example.test",
                QueryPort: 27015,
                RconPort: null,
                PollIntervalSeconds: 30,
                Notes: null,
                IsEnabled: false));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(created);
        var firstUpdate = new UpdateServerRequest(
            created.Revision,
            "First edit",
            created.Host,
            created.QueryPort,
            created.RconPort,
            created.PollIntervalSeconds,
            created.Notes);
        using var accepted = await client.PatchAsJsonAsync(
            $"/api/servers/{created.Id}",
            firstUpdate);
        accepted.EnsureSuccessStatusCode();

        using var stale = await client.PatchAsJsonAsync(
            $"/api/servers/{created.Id}",
            firstUpdate with { Name = "Stale edit" });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadAsStringAsync();
        Assert.Contains("server_update.revision_conflict", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisableServer_and_enableServer_update_server_enabled_flag()
    {
        await using var factory = new GoldSrcOpsApiFactory();
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
        Assert.NotNull(created);

        var disableResponse = await client.PostAsync($"/api/servers/{created.Id}/disable", content: null);

        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);
        var disabled = await disableResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(disabled);
        Assert.False(disabled.IsEnabled);
        Assert.Equal(created.Revision + 1, disabled.Revision);

        var enableResponse = await client.PostAsync($"/api/servers/{created.Id}/enable", content: null);

        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);
        var enabled = await enableResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(enabled);
        Assert.True(enabled.IsEnabled);
        Assert.Equal(created.Id, enabled.Id);
        Assert.Equal(disabled.Revision + 1, enabled.Revision);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public async Task EnableDisableServer_returns_not_found_for_missing_server(string action)
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/servers/{Guid.NewGuid()}/{action}", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
}
