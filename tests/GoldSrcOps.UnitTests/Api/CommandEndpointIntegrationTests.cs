using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task SetRconCredential_returns_metadata_without_secret_alias()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var request = new SetRconCredentialRequest(1, 0, "server_1_rcon");

        var response = await client.PutAsJsonAsync($"/api/servers/{server.Id}/credentials/rcon", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain(request.SecretAlias);
        var credential = await response.Content.ReadFromJsonAsync<ServerCredentialResponse>();
        credential.Should().NotBeNull();
        credential.Should().BeEquivalentTo(new
        {
            ServerId = server.Id,
            Revision = 1L,
            Kind = "RconPassword",
            IsConfigured = true,
            UpdatedAtUtc = (DateTimeOffset?)null
        });
    }

    [Theory]
    [InlineData("ConnectionStrings:GoldSrcOps")]
    [InlineData("nested/alias")]
    [InlineData("-starts-with-symbol")]
    public async Task SetRconCredential_rejects_unsafe_secret_alias(string secretAlias)
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);

        var response = await client.PutAsJsonAsync(
            $"/api/servers/{server.Id}/credentials/rcon",
            new SetRconCredentialRequest(1, 0, secretAlias));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetRconCredential_requires_paused_monitoring()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var request = new RegisterServerRequest(
            "Active server",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: 27015,
            PollIntervalSeconds: 30,
            Notes: null,
            IsEnabled: true);
        var registerResponse = await client.PostAsJsonAsync("/api/servers", request);
        var server = await registerResponse.Content.ReadFromJsonAsync<ServerResponse>();

        var response = await client.PutAsJsonAsync(
            $"/api/servers/{server!.Id}/credentials/rcon",
            new SetRconCredentialRequest(server.Revision, 0, "active_server"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(response)).Should().Be(
            "rcon_credential.monitoring_must_be_paused");
    }

    [Fact]
    public async Task SetRconCredential_rejects_stale_revision_and_incomplete_command()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var credential = await SetRconCredentialAsync(client, server.Id);

        var staleResponse = await client.PutAsJsonAsync(
            $"/api/servers/{server.Id}/credentials/rcon",
            new SetRconCredentialRequest(2, 0, "rotated_alias"));
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(staleResponse)).Should().Be(
            "rcon_credential.revision_conflict");

        var queueResponse = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/commands/say",
            new SayCommandRequest("hold rotation"));
        queueResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var blockedResponse = await client.PutAsJsonAsync(
            $"/api/servers/{server.Id}/credentials/rcon",
            new SetRconCredentialRequest(2, credential.Revision, "rotated_alias"));
        blockedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadProblemCodeAsync(blockedResponse)).Should().Be(
            "rcon_credential.commands_in_progress");

        var currentResponse = await client.GetAsync($"/api/servers/{server.Id}/credentials");
        var currentJson = await currentResponse.Content.ReadAsStringAsync();
        currentJson.Should().NotContain("server_rcon").And.NotContain("rotated_alias");
        var current = await currentResponse.Content.ReadFromJsonAsync<ServerCredentialResponse[]>();
        current.Should().ContainSingle().Which.Revision.Should().Be(1);
    }

    [Fact]
    public async Task QueueCommand_returns_conflict_when_rcon_credential_is_missing()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var request = new SayCommandRequest("hello");

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
        var request = new ChangeMapCommandRequest("de_dust2");

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
    public async Task Background_dispatcher_executes_pending_command_through_configured_executor()
    {
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("fake dispatch accepted"));
        await using var factory = new GoldSrcOpsApiFactory(
            services =>
            {
                services.RemoveAll<IRconCommandExecutor>();
                services.AddSingleton<IRconCommandExecutor>(executor);
            },
            commandDispatcherEnabled: true);
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var createResponse = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/commands/say",
            new SayCommandRequest("hello"));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        created.Should().NotBeNull();

        var dispatched = await WaitForTerminalCommandAsync(client, created!.Id);

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
        dispatched.StartedAtUtc.Should().NotBeNull();
        dispatched.CompletedAtUtc.Should().NotBeNull();
        executor.CallCount.Should().Be(1);
        executor.LastRequest.Should().BeEquivalentTo(new
        {
            CommandId = created.Id,
            ServerId = server.Id,
            Host = "127.0.0.1",
            Port = 27015,
            CredentialSecretReference = "rcon-secret://server_rcon",
            Type = GoldSrcOps.Domain.Commands.ServerCommandType.Say,
            CommandText = "say hello"
        });

        var json = await client.GetStringAsync($"/api/commands/{created.Id}");
        json.Should().NotContain("rcon-secret://server_rcon");
    }

    [Fact]
    public async Task Background_dispatcher_marks_command_failed_when_default_secret_reference_is_missing()
    {
        await using var factory = new GoldSrcOpsApiFactory(commandDispatcherEnabled: true);
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var createResponse = await client.PostAsync(
            $"/api/servers/{server.Id}/commands/restart",
            content: null);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        created.Should().NotBeNull();

        var dispatched = await WaitForTerminalCommandAsync(client, created!.Id);

        dispatched.Status.Should().Be("Failed");
        dispatched.FailureReason.Should().Be("RCON credential secret could not be resolved.");
        dispatched.ResultSummary.Should().BeNull();
    }

    [Fact]
    public async Task QueueRawCommand_returns_validation_problem_for_empty_command_text()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id);
        var request = new RawCommandRequest(" ");

        var response = await client.PostAsJsonAsync($"/api/servers/{server.Id}/commands/raw", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<CommandExecutionResponse> WaitForTerminalCommandAsync(
        HttpClient client,
        Guid commandId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var command = await client.GetFromJsonAsync<CommandExecutionResponse>(
                $"/api/commands/{commandId}",
                timeout.Token);
            command.Should().NotBeNull();

            if (command!.Status is "Succeeded" or "Failed")
            {
                return command;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static async Task<ServerResponse> RegisterServerAsync(HttpClient client)
    {
        var request = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: 27015,
            PollIntervalSeconds: 30,
            Notes: null,
            IsEnabled: false);
        var response = await client.PostAsJsonAsync("/api/servers", request);
        response.EnsureSuccessStatusCode();
        var server = await response.Content.ReadFromJsonAsync<ServerResponse>();

        return server!;
    }

    private static async Task<ServerCredentialResponse> SetRconCredentialAsync(
        HttpClient client,
        Guid serverId)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/servers/{serverId}/credentials/rcon",
            new SetRconCredentialRequest(1, 0, "server_rcon"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ServerCredentialResponse>())!;
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("code").GetString();
    }
}
