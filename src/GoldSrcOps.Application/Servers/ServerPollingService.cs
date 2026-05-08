using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed class ServerPollingService
{
    private readonly IServerRepository _servers;
    private readonly IIncidentRepository _incidents;
    private readonly IGoldSrcServerQueryClient _queryClient;
    private readonly IClock _clock;
    private readonly ServerPollingSettings _settings;

    public ServerPollingService(
        IServerRepository servers,
        IIncidentRepository incidents,
        IGoldSrcServerQueryClient queryClient,
        IClock clock,
        ServerPollingSettings settings)
    {
        _servers = servers;
        _incidents = incidents;
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
        var openedIncidents = 0;
        var closedIncidents = 0;

        foreach (var server in dueServers)
        {
            var outcome = await PollServerAsync(server, cancellationToken);
            if (outcome.Succeeded)
            {
                successfulPolls++;
            }
            else
            {
                failedPolls++;
            }

            if (outcome.OpenedIncident)
            {
                openedIncidents++;
            }

            if (outcome.ClosedIncident)
            {
                closedIncidents++;
            }

            await _servers.SaveChangesAsync(cancellationToken);
        }

        var result = new ServerPollingResult(
            dueServers.Length,
            successfulPolls,
            failedPolls,
            openedIncidents,
            closedIncidents);

        GoldSrcOpsMetrics.RecordPollingRun(result);

        return result;
    }

    private async Task<ServerPollOutcome> PollServerAsync(Server server, CancellationToken cancellationToken)
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
            var closedIncident = await CloseOpenIncidentAsync(server.Id, checkedAtUtc, cancellationToken);

            return new ServerPollOutcome(Succeeded: true, OpenedIncident: false, ClosedIncident: closedIncident);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var openedIncident = await MarkFailedAsync(
                server,
                $"Query timed out after {_settings.QueryTimeout.TotalMilliseconds:0} ms.",
                cancellationToken);

            return new ServerPollOutcome(Succeeded: false, openedIncident, ClosedIncident: false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var openedIncident = await MarkFailedAsync(server, ex.Message, cancellationToken);
            return new ServerPollOutcome(Succeeded: false, openedIncident, ClosedIncident: false);
        }
    }

    private async Task<bool> MarkFailedAsync(
        Server server,
        string failureReason,
        CancellationToken cancellationToken)
    {
        var checkedAtUtc = _clock.UtcNow;
        var snapshot = PollSnapshot.Unreachable(server.Id, checkedAtUtc, failureReason);

        var state = server.GetCurrentState(checkedAtUtc);
        state.MarkOffline(checkedAtUtc, failureReason);
        await _servers.AddSnapshotAsync(snapshot, cancellationToken);

        if (state.ConsecutiveFailures < _settings.IncidentFailureThreshold)
        {
            return false;
        }

        var openIncident = await _incidents.GetOpenForServerAsync(server.Id, cancellationToken);
        if (openIncident is not null)
        {
            openIncident.RecordFailure(state.ConsecutiveFailures);
            return false;
        }

        var incident = AvailabilityIncident.Open(
            server.Id,
            checkedAtUtc,
            failureReason,
            state.ConsecutiveFailures);

        await _incidents.AddAsync(incident, cancellationToken);
        return true;
    }

    private async Task<bool> CloseOpenIncidentAsync(
        Guid serverId,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        var openIncident = await _incidents.GetOpenForServerAsync(serverId, cancellationToken);
        if (openIncident is null)
        {
            return false;
        }

        openIncident.Close(checkedAtUtc, "Server query recovered.");
        return true;
    }

    private static int ToLatencyMs(TimeSpan latency)
    {
        if (latency.TotalMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(0, (int)Math.Round(latency.TotalMilliseconds));
    }

    private sealed record ServerPollOutcome(
        bool Succeeded,
        bool OpenedIncident,
        bool ClosedIncident);
}
