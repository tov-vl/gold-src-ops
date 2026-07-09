using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlCommandEndpointIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Command_dispatch_persists_credentials_and_execution_status_through_postgresql_provider()
    {
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("postgres fake dispatch"));
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(services =>
        {
            services.RemoveAll<IRconCommandExecutor>();
            services.AddSingleton<IRconCommandExecutor>(executor);
        });
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var credentialRequest = new SetRconCredentialRequest("dev-secrets://goldsrcops/server-1/rcon");
        var credentialResponse = await client.PutAsJsonAsync(
            $"/api/servers/{server.Id}/credentials/rcon",
            credentialRequest);
        credentialResponse.EnsureSuccessStatusCode();
        var commandRequest = new SayCommandRequest("hello players", "admin");

        var response = await client.PostAsJsonAsync($"/api/servers/{server.Id}/commands/say", commandRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var command = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        command.Should().NotBeNull();
        var dispatchResponse = await client.PostAsync($"/api/commands/{command!.Id}/dispatch", content: null);
        dispatchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var persisted = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var credential = await dbContext.ServerCredentials
                .AsNoTracking()
                .SingleAsync(x => x.ServerId == server.Id);
            var execution = await dbContext.CommandExecutions
                .AsNoTracking()
                .SingleAsync(x => x.Id == command!.Id);

            return new PersistedCommandFoundation(
                credential.ServerId,
                credential.Kind,
                credential.SecretReference,
                execution.ServerId,
                execution.Type,
                execution.Status,
                execution.Payload,
                execution.RequestedBy,
                execution.ResultSummary,
                execution.FailureReason);
        });

        persisted.Should().BeEquivalentTo(new PersistedCommandFoundation(
            server.Id,
            ServerCredentialKind.RconPassword,
            credentialRequest.SecretReference,
            server.Id,
            ServerCommandType.Say,
            CommandExecutionStatus.Succeeded,
            commandRequest.Message,
            commandRequest.RequestedBy,
            ResultSummary: "postgres fake dispatch",
            FailureReason: null));
        executor.LastRequest.Should().BeEquivalentTo(new
        {
            CommandId = command.Id,
            ServerId = server.Id,
            Host = "127.0.0.1",
            Port = 27015,
            CredentialSecretReference = credentialRequest.SecretReference,
            Type = ServerCommandType.Say,
            CommandText = "say hello players"
        });
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

    private sealed record PersistedCommandFoundation(
        Guid CredentialServerId,
        ServerCredentialKind Kind,
        string SecretReference,
        Guid CommandServerId,
        ServerCommandType Type,
        CommandExecutionStatus Status,
        string? Payload,
        string? RequestedBy,
        string? ResultSummary,
        string? FailureReason);
}
