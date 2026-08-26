using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.UnitTests.Api;

public sealed class SecurityEndpointIntegrationTests
{
    private const string ExistingId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("POST", "/api/servers")]
    [InlineData("PATCH", $"/api/servers/{ExistingId}")]
    [InlineData("POST", $"/api/servers/{ExistingId}/enable")]
    [InlineData("POST", $"/api/servers/{ExistingId}/disable")]
    [InlineData("PUT", $"/api/servers/{ExistingId}/credentials/rcon")]
    [InlineData("POST", $"/api/servers/{ExistingId}/commands/change-map")]
    [InlineData("POST", $"/api/servers/{ExistingId}/commands/restart")]
    [InlineData("POST", $"/api/servers/{ExistingId}/commands/say")]
    [InlineData("POST", $"/api/servers/{ExistingId}/commands/raw")]
    [InlineData("POST", $"/api/alert-delivery/dead-letters/{ExistingId}/replay")]
    public async Task Reader_cannot_call_mutation_endpoints(string method, string path)
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { })
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reader_can_call_read_and_metrics_endpoints()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            client.GetAsync("/api/servers"),
            client.GetAsync("/api/incidents/open"),
            client.GetAsync("/api/dashboard/overview"),
            client.GetAsync("/api/alert-delivery/dead-letters"),
            client.GetAsync("/metrics"));
        var replay = await client.GetAsync($"/api/alert-delivery/replays/{ExistingId}");

        responses.Should().OnlyContain(static response => response.StatusCode == HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_client_can_call_health_only()
    {
        await using var factory = new GoldSrcOpsApiFactory(principal: TestApiPrincipal.Anonymous);
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");
        var read = await client.GetAsync("/api/servers");
        var alertDeliveryRead = await client.GetAsync("/api/alert-delivery/dead-letters");
        var alertDeliveryDetail = await client.GetAsync(
            $"/api/alert-delivery/dead-letters/{ExistingId}");
        var alertDeliveryReplay = await client.GetAsync(
            $"/api/alert-delivery/replays/{ExistingId}");
        var mutation = await client.PostAsJsonAsync("/api/servers", new { });
        var replayMutation = await client.PostAsJsonAsync(
            $"/api/alert-delivery/dead-letters/{ExistingId}/replay",
            new { reason = "endpoint restored" });
        var metrics = await client.GetAsync("/metrics");

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        alertDeliveryRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        alertDeliveryDetail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        alertDeliveryReplay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        mutation.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        replayMutation.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        metrics.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_client_without_application_role_is_forbidden()
    {
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.WithoutRoles());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/servers");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_without_subject_is_rejected()
    {
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.WithoutSubject());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/servers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Operator_can_call_read_and_mutation_endpoints()
    {
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.Operator("operator-42"));
        using var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/servers", CreateServerRequest());
        var read = await client.GetAsync("/api/servers");
        var alertDeliveryRead = await client.GetAsync("/api/alert-delivery/dead-letters");
        var replayRead = await client.GetAsync($"/api/alert-delivery/replays/{ExistingId}");
        var replayMutation = await client.PostAsJsonAsync(
            $"/api/alert-delivery/dead-letters/{ExistingId}/replay",
            new { reason = "endpoint restored" });

        register.StatusCode.Should().Be(HttpStatusCode.Created);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        alertDeliveryRead.StatusCode.Should().Be(HttpStatusCode.OK);
        replayRead.StatusCode.Should().Be(HttpStatusCode.NotFound);
        replayMutation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueueCommand_uses_authenticated_subject_and_ignores_spoofed_requested_by()
    {
        const string subject = "operator-42";
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.Operator(subject));
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var credentialResponse = await client.PutAsJsonAsync(
            $"/api/servers/{server.Id}/credentials/rcon",
            new SetRconCredentialRequest("server_rcon"));
        credentialResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/commands/say",
            new { message = "hello", requestedBy = "spoofed-client-value" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var command = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        command.Should().NotBeNull();
        command!.RequestedBy.Should().Be(subject);
        var persistedRequestedBy = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.CommandExecutions
                .AsNoTracking()
                .Where(execution => execution.Id == command.Id)
                .Select(static execution => execution.RequestedBy)
                .SingleAsync());
        persistedRequestedBy.Should().Be(subject);
    }

    private static RegisterServerRequest CreateServerRequest() =>
        new(
            "Security Test Server",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: 27015,
            PollIntervalSeconds: 30,
            Notes: null);

    private static async Task<ServerResponse> RegisterServerAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/servers", CreateServerRequest());
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ServerResponse>())!;
    }
}
