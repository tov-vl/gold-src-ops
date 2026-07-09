using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Api;

public sealed class CommandEndpointIntegrationTests
{
    [Fact]
    public async Task SetRconCredential_returns_metadata_without_secret_reference()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var request = new SetRconCredentialRequest("dev-secrets://goldsrcops/server-1/rcon");

        var response = await client.PutAsJsonAsync($"/api/servers/{server.Id}/credentials/rcon", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain(request.SecretReference);
        var credential = await response.Content.ReadFromJsonAsync<ServerCredentialResponse>();
        credential.Should().NotBeNull();
        credential.Should().BeEquivalentTo(new
        {
            ServerId = server.Id,
            Kind = "RconPassword",
            IsConfigured = true,
            UpdatedAtUtc = (DateTimeOffset?)null
        });
    }

    [Fact]
    public async Task QueueCommand_returns_conflict_when_rcon_credential_is_missing()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var request = new SayCommandRequest("hello", "admin");

        var response = await client.PostAsJsonAsync($"/api/servers/{server.Id}/commands/say", request);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task QueueCommand_creates_pending_execution_and_exposes_history()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var request = new ChangeMapCommandRequest("de_dust2", "admin");

        var response = await client.PostAsJsonAsync($"/api/servers/{server.Id}/commands/change-map", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var command = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        command.Should().NotBeNull();
        command.Should().BeEquivalentTo(new
        {
            ServerId = server.Id,
            Type = "ChangeMap",
            Status = "Pending",
            Payload = "de_dust2",
            RequestedBy = "admin",
            StartedAtUtc = (DateTimeOffset?)null,
            CompletedAtUtc = (DateTimeOffset?)null,
            ResultSummary = (string?)null,
            FailureReason = (string?)null
        });

        var getResponse = await client.GetAsync($"/api/commands/{command!.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var fromGet = await getResponse.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        fromGet.Should().BeEquivalentTo(command);

        var listResponse = await client.GetAsync($"/api/servers/{server.Id}/commands?limit=10");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await listResponse.Content.ReadFromJsonAsync<CommandExecutionResponse[]>();
        history.Should().ContainSingle().Which.Should().BeEquivalentTo(command);
    }

    [Fact]
    public async Task DispatchCommand_executes_pending_command_through_configured_executor()
    {
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("fake dispatch accepted"));
        await using var factory = new GoldSrcOpsApiFactory(services =>
        {
            services.RemoveAll<IRconCommandExecutor>();
            services.AddSingleton<IRconCommandExecutor>(executor);
        });
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/commands/say",
            new SayCommandRequest("hello", "admin"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        created.Should().NotBeNull();

        var response = await client.PostAsync($"/api/commands/{created!.Id}/dispatch", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("dev-secrets://goldsrcops/server/rcon");
        var dispatched = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        dispatched.Should().NotBeNull();
        dispatched.Should().BeEquivalentTo(new
        {
            created.Id,
            ServerId = server.Id,
            Type = "Say",
            Status = "Succeeded",
            Payload = "hello",
            RequestedBy = "admin",
            ResultSummary = "fake dispatch accepted",
            FailureReason = (string?)null
        });
        dispatched!.StartedAtUtc.Should().NotBeNull();
        dispatched.CompletedAtUtc.Should().NotBeNull();
        executor.CallCount.Should().Be(1);
        executor.LastRequest.Should().BeEquivalentTo(new
        {
            CommandId = created.Id,
            ServerId = server.Id,
            Host = "127.0.0.1",
            Port = 27015,
            CredentialSecretReference = "dev-secrets://goldsrcops/server/rcon",
            Type = GoldSrcOps.Domain.Commands.ServerCommandType.Say,
            CommandText = "say hello"
        });

        var repeatResponse = await client.PostAsync($"/api/commands/{created.Id}/dispatch", content: null);

        repeatResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        executor.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchCommand_marks_command_failed_when_default_executor_is_not_configured()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/commands/restart",
            new RestartServerCommandRequest("admin"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        created.Should().NotBeNull();

        var response = await client.PostAsync($"/api/commands/{created!.Id}/dispatch", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dispatched = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        dispatched.Should().NotBeNull();
        dispatched!.Status.Should().Be("Failed");
        dispatched.FailureReason.Should().Be("RCON executor is not configured.");
        dispatched.ResultSummary.Should().BeNull();
    }

    [Fact]
    public async Task QueueRawCommand_returns_validation_problem_for_empty_command_text()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var request = new RawCommandRequest(" ", "admin");

        var response = await client.PostAsJsonAsync($"/api/servers/{server.Id}/commands/raw", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<ServerResponse> RegisterServerAsync(HttpClient client)
    {
        var request = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: 27015,
            PollIntervalSeconds: 30,
            Notes: null);
        var response = await client.PostAsJsonAsync("/api/servers", request);
        response.EnsureSuccessStatusCode();
        var server = await response.Content.ReadFromJsonAsync<ServerResponse>();

        return server!;
    }

    private static async Task SetRconCredentialAsync(HttpClient client, Guid serverId)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/servers/{serverId}/credentials/rcon",
            new SetRconCredentialRequest("dev-secrets://goldsrcops/server/rcon"));
        response.EnsureSuccessStatusCode();
    }
}
