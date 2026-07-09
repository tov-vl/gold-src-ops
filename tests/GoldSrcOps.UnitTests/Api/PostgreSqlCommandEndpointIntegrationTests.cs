using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlCommandEndpointIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Command_foundation_persists_credentials_and_command_history_through_postgresql_provider()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
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
                execution.RequestedBy);
        });

        persisted.Should().BeEquivalentTo(new PersistedCommandFoundation(
            server.Id,
            ServerCredentialKind.RconPassword,
            credentialRequest.SecretReference,
            server.Id,
            ServerCommandType.Say,
            CommandExecutionStatus.Pending,
            commandRequest.Message,
            commandRequest.RequestedBy));
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
        string? RequestedBy);
}
