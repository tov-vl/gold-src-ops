using System.Security.Cryptography;
using System.Text.Json;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed class ServersService
{
    private readonly IServerRepository _servers;
    private readonly IClock _clock;

    public ServersService(IServerRepository servers, IClock clock)
    {
        _servers = servers;
        _clock = clock;
    }

    public async Task<RegisterServerResult> RegisterAsync(
        RegisterServerCommand command,
        CancellationToken cancellationToken)
    {
        var endpoint = new ServerEndpoint(command.Host, command.QueryPort, command.RconPort);
        var intentHash = command.RegistrationRequestId is null
            ? null
            : CreateRegistrationIntentHash(command, endpoint);
        var createdAtUtc = _clock.UtcNow;
        createdAtUtc = createdAtUtc.AddTicks(-(createdAtUtc.Ticks % TimeSpan.TicksPerMicrosecond));
        var server = new Server(
            command.Name,
            command.Game,
            endpoint,
            command.PollIntervalSeconds,
            command.Notes,
            createdAtUtc,
            command.IsEnabled,
            command.RegistrationRequestId,
            intentHash);

        var persistenceResult = await _servers.RegisterAsync(server, cancellationToken);
        var persistedServer = persistenceResult.Server;

        if (persistenceResult.WasCreated)
        {
            return RegisterServerResult.Created(Map(persistedServer));
        }

        return string.Equals(
            persistedServer.RegistrationIntentHash,
            intentHash,
            StringComparison.Ordinal)
            ? RegisterServerResult.Idempotent(Map(persistedServer))
            : RegisterServerResult.IdempotencyConflict();
    }

    public Task<ServerDto?> EnableAsync(Guid id, CancellationToken cancellationToken)
    {
        return SetEnabledAsync(id, isEnabled: true, cancellationToken);
    }

    public Task<ServerDto?> DisableAsync(Guid id, CancellationToken cancellationToken)
    {
        return SetEnabledAsync(id, isEnabled: false, cancellationToken);
    }

    public async Task<ServerDto?> UpdateAsync(
        Guid id,
        UpdateServerCommand command,
        CancellationToken cancellationToken)
    {
        var server = await _servers.GetForUpdateAsync(id, cancellationToken);
        if (server is null)
        {
            return null;
        }

        server.UpdateDetails(
            command.Name,
            new ServerEndpoint(command.Host, command.QueryPort, command.RconPort),
            command.PollIntervalSeconds,
            command.Notes);

        await _servers.SaveChangesAsync(cancellationToken);

        return Map(server);
    }

    public async Task<IReadOnlyList<ServerDto>> ListAsync(CancellationToken cancellationToken)
    {
        var servers = await _servers.ListAsync(cancellationToken);
        return servers.Select(Map).ToArray();
    }

    public async Task<ServerDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var server = await _servers.GetAsync(id, cancellationToken);
        return server is null ? null : Map(server);
    }

    public async Task<ServerStatusDto?> GetStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var server = await _servers.GetAsync(id, cancellationToken);
        var state = server?.CurrentState;

        return state is null
            ? null
            : new ServerStatusDto(
                state.ServerId,
                state.Status,
                state.IsReachable,
                state.LastCheckedAtUtc,
                state.LastSuccessAtUtc,
                state.LatencyMs,
                state.CurrentMap,
                state.Players,
                state.MaxPlayers,
                state.FailureReason,
                state.ConsecutiveFailures);
    }

    private async Task<ServerDto?> SetEnabledAsync(
        Guid id,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var server = await _servers.GetForUpdateAsync(id, cancellationToken);
        if (server is null)
        {
            return null;
        }

        if (isEnabled)
        {
            server.Enable();
        }
        else
        {
            server.Disable();
        }

        await _servers.SaveChangesAsync(cancellationToken);

        return Map(server);
    }

    private static ServerDto Map(Server server) =>
        new(
            server.Id,
            server.Name,
            server.Game,
            server.Endpoint.Host,
            server.Endpoint.QueryPort,
            server.Endpoint.RconPort,
            server.IsEnabled,
            server.PollIntervalSeconds,
            server.Notes,
            server.CreatedAtUtc);

    private static string CreateRegistrationIntentHash(
        RegisterServerCommand command,
        ServerEndpoint endpoint)
    {
        var normalizedNotes = string.IsNullOrWhiteSpace(command.Notes)
            ? null
            : command.Notes.Trim();
        var intent = new RegistrationIntent(
            command.Name.Trim(),
            command.Game,
            endpoint.Host.ToUpperInvariant(),
            endpoint.QueryPort,
            endpoint.RconPort,
            command.PollIntervalSeconds,
            normalizedNotes,
            command.IsEnabled);
        var payload = JsonSerializer.SerializeToUtf8Bytes(intent);

        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private sealed record RegistrationIntent(
        string Name,
        GameServerKind Game,
        string Host,
        int QueryPort,
        int? RconPort,
        int PollIntervalSeconds,
        string? Notes,
        bool IsEnabled);
}
