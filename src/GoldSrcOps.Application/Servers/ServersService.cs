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

    public async Task<ServerDto> RegisterAsync(RegisterServerCommand command, CancellationToken cancellationToken)
    {
        var server = new Server(
            command.Name,
            command.Game,
            new ServerEndpoint(command.Host, command.QueryPort, command.RconPort),
            command.PollIntervalSeconds,
            command.Notes,
            _clock.UtcNow);

        await _servers.AddAsync(server, cancellationToken);
        await _servers.SaveChangesAsync(cancellationToken);

        return Map(server);
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
}
