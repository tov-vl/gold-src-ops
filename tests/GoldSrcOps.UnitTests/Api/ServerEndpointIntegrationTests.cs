using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Servers;

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
            Notes: "before");
        var createResponse = await client.PostAsJsonAsync("/api/servers", createRequest);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<ServerResponse>();
        Assert.NotNull(created);
        var updateRequest = new UpdateServerRequest(
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
        Assert.Equal("Inferno Public", updated.Name);
        Assert.Equal("GoldSrc", updated.Game);
        Assert.Equal("cs.example.local", updated.Host);
        Assert.Equal(27016, updated.QueryPort);
        Assert.Equal(27017, updated.RconPort);
        Assert.True(updated.IsEnabled);
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
            " ",
            "",
            QueryPort: 0,
            RconPort: 70000,
            PollIntervalSeconds: 0,
            Notes: null);

        var response = await client.PatchAsJsonAsync($"/api/servers/{Guid.NewGuid()}", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
