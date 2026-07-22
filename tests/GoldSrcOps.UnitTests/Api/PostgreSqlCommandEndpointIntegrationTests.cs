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
    public async Task Command_dispatcher_persists_credentials_and_execution_status_through_postgresql_provider()
    {
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("postgres fake dispatch"));
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(services =>
        {
            services.RemoveAll<IRconCommandExecutor>();
            services.AddSingleton<IRconCommandExecutor>(executor);
        });
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        var credentialRequest = new SetRconCredentialRequest("server_1_rcon");
        var secretReference = RconSecretReference.Create(credentialRequest.SecretAlias);
        var credentialResponse = await client.PutAsJsonAsync(
            $"/api/servers/{server.Id}/credentials/rcon",
            credentialRequest);
        credentialResponse.EnsureSuccessStatusCode();
        var commandRequest = new SayCommandRequest("hello players");

        var response = await client.PostAsJsonAsync($"/api/servers/{server.Id}/commands/say", commandRequest);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var command = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();
        command.Should().NotBeNull();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<CommandDispatcher>();
            var dispatchResult = await dispatcher.DispatchNextAsync(CancellationToken.None);
            dispatchResult.Kind.Should().Be(CommandDispatchAttemptResultKind.Completed);
            dispatchResult.CommandId.Should().Be(command!.Id);
        }

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
            secretReference,
            server.Id,
            ServerCommandType.Say,
            CommandExecutionStatus.Succeeded,
            commandRequest.Message,
            RequestedBy: "admin",
            ResultSummary: "postgres fake dispatch",
            FailureReason: null));
        executor.LastRequest.Should().BeEquivalentTo(new
        {
            CommandId = command.Id,
            ServerId = server.Id,
            Host = "127.0.0.1",
            Port = 27015,
            CredentialSecretReference = secretReference,
            Type = ServerCommandType.Say,
            CommandText = "say hello players"
        });
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_claims_serialize_commands_per_server()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var firstServer = await RegisterServerAsync(client, "Dust2 Public", 27015);
        var secondServer = await RegisterServerAsync(client, "Inferno Public", 27016);
        await SetRconCredentialAsync(client, firstServer.Id, "server_1_rcon");
        await SetRconCredentialAsync(client, secondServer.Id, "server_2_rcon");
        await QueueRestartAsync(client, firstServer.Id);
        await QueueRestartAsync(client, firstServer.Id);
        await QueueRestartAsync(client, secondServer.Id);
        var startedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<ICommandExecutionRepository>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<ICommandExecutionRepository>();

        var claims = await Task.WhenAll(
            firstRepository.ClaimNextPendingAsync(startedAtUtc, CancellationToken.None),
            secondRepository.ClaimNextPendingAsync(startedAtUtc, CancellationToken.None));

        claims.All(claim => claim is not null).Should().BeTrue();
        claims.Select(claim => claim!.Command.ServerId).Distinct().Should().HaveCount(2);

        var statuses = await factory.ExecuteDbContextAsync(dbContext =>
            dbContext.CommandExecutions
                .AsNoTracking()
                .Select(command => new { command.ServerId, command.Status })
                .ToListAsync());

        statuses.Count(command => command.Status == CommandExecutionStatus.Running).Should().Be(2);
        statuses.Count(command => command.Status == CommandExecutionStatus.Pending).Should().Be(1);
        statuses
            .Where(command => command.Status == CommandExecutionStatus.Running)
            .GroupBy(command => command.ServerId)
            .Should()
            .OnlyContain(group => group.Count() == 1);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Interrupted_command_is_failed_and_next_server_command_becomes_claimable()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var server = await RegisterServerAsync(client);
        await SetRconCredentialAsync(client, server.Id, "server_1_rcon");
        await QueueRestartAsync(client, server.Id);
        await QueueRestartAsync(client, server.Id);
        var startedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        Guid interruptedCommandId;

        await using (var claimScope = factory.Services.CreateAsyncScope())
        {
            var repository = claimScope.ServiceProvider.GetRequiredService<ICommandExecutionRepository>();
            var claimed = await repository.ClaimNextPendingAsync(startedAtUtc, CancellationToken.None);
            claimed.Should().NotBeNull();
            interruptedCommandId = claimed!.Command.Id;
        }

        await using (var recoveryScope = factory.Services.CreateAsyncScope())
        {
            var repository = recoveryScope.ServiceProvider.GetRequiredService<ICommandExecutionRepository>();
            var recovered = await repository.FailInterruptedAsync(
                startedAtUtc.AddSeconds(1),
                startedAtUtc.AddSeconds(2),
                CommandDispatcher.InterruptedFailureReason,
                CancellationToken.None);
            recovered.Should().Be(1);
        }

        Guid nextCommandId;
        await using (var nextClaimScope = factory.Services.CreateAsyncScope())
        {
            var repository = nextClaimScope.ServiceProvider.GetRequiredService<ICommandExecutionRepository>();
            var next = await repository.ClaimNextPendingAsync(
                startedAtUtc.AddSeconds(3),
                CancellationToken.None);
            next.Should().NotBeNull();
            nextCommandId = next!.Command.Id;
        }

        nextCommandId.Should().NotBe(interruptedCommandId);
        var interrupted = await factory.ExecuteDbContextAsync(dbContext =>
            dbContext.CommandExecutions
                .AsNoTracking()
                .SingleAsync(command => command.Id == interruptedCommandId));
        interrupted.Status.Should().Be(CommandExecutionStatus.Failed);
        interrupted.CompletedAtUtc.Should().Be(startedAtUtc.AddSeconds(2));
        interrupted.FailureReason.Should().Be(CommandDispatcher.InterruptedFailureReason);
    }

    private static async Task SetRconCredentialAsync(
        HttpClient client,
        Guid serverId,
        string secretAlias)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/servers/{serverId}/credentials/rcon",
            new SetRconCredentialRequest(secretAlias));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<CommandExecutionResponse> QueueRestartAsync(
        HttpClient client,
        Guid serverId)
    {
        var response = await client.PostAsync(
            $"/api/servers/{serverId}/commands/restart",
            content: null);
        response.EnsureSuccessStatusCode();
        var command = await response.Content.ReadFromJsonAsync<CommandExecutionResponse>();

        return command!;
    }

    private static async Task<ServerResponse> RegisterServerAsync(
        HttpClient client,
        string name = "Dust2 Public",
        int port = 27015)
    {
        var request = new RegisterServerRequest(
            name,
            "127.0.0.1",
            QueryPort: port,
            RconPort: port,
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
