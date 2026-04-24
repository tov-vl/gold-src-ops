using GoldSrcOps.Application.Common;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed class ServerPollingService
{
    private readonly IServerRepository _servers;
    private readonly IGoldSrcServerQueryClient _queryClient;
    private readonly IClock _clock;
    private readonly ServerPollingSettings _settings;

    public ServerPollingService(
        IServerRepository servers,
        IGoldSrcServerQueryClient queryClient,
        IClock clock,
        ServerPollingSettings settings)
    {
        _servers = servers;
        _queryClient = queryClient;
        _clock = clock;
        _settings = settings;
    }

    public async Task<ServerPollingResult> PollDueServersAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var enabledServers = await _servers.ListEnabledAsync(cancellationToken);
        var dueServers = enabledServers
            .Where(server => server.IsDueForPolling(now))
            .Take(_settings.BatchSize)
            .ToArray();

        var successfulPolls = 0;
        var failedPolls = 0;

        foreach (var server in dueServers)
        {
            var succeeded = await PollServerAsync(server, cancellationToken);
            if (succeeded)
            {
                successfulPolls++;
            }
            else
            {
                failedPolls++;
            }

            await _servers.SaveChangesAsync(cancellationToken);
        }

        return new ServerPollingResult(dueServers.Length, successfulPolls, failedPolls);
    }

    private async Task<bool> PollServerAsync(Server server, CancellationToken cancellationToken)
    {
        try
        {
            var info = await _queryClient.QueryInfoAsync(
                new GameServerEndpoint(server.Endpoint.Host, server.Endpoint.QueryPort, _settings.QueryTimeout),
                cancellationToken);

            var checkedAtUtc = _clock.UtcNow;
            var latencyMs = ToLatencyMs(info.Latency);
            var snapshot = PollSnapshot.Reachable(
                server.Id,
                checkedAtUtc,
                latencyMs,
                info.Map,
                info.Players,
                info.MaxPlayers,
                info.Bots,
                info.Version);

            server.GetCurrentState(checkedAtUtc).MarkOnline(
                checkedAtUtc,
                latencyMs,
                info.Map,
                info.Players,
                info.MaxPlayers);

            await _servers.AddSnapshotAsync(snapshot, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(
                server,
                $"Query timed out after {_settings.QueryTimeout.TotalMilliseconds:0} ms.",
                cancellationToken);

            return false;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(server, ex.Message, cancellationToken);
            return false;
        }
    }

    private async Task MarkFailedAsync(
        Server server,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var checkedAtUtc = _clock.UtcNow;
        var snapshot = PollSnapshot.Unreachable(server.Id, checkedAtUtc, failureReason);

        server.GetCurrentState(checkedAtUtc).MarkOffline(checkedAtUtc, failureReason);
        await _servers.AddSnapshotAsync(snapshot, cancellationToken);
    }

    private static int ToLatencyMs(TimeSpan latency)
    {
        if (latency.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(0, (int)Math.Round(latency.TotalMilliseconds));
    }
}
