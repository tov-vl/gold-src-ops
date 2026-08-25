using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Servers;

public sealed class ServerPollingService
{
    private readonly IServerRepository _servers;
    private readonly IIncidentRepository _incidents;
    private readonly IOutboxWriter _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGoldSrcServerQueryClient _queryClient;
    private readonly IClock _clock;
    private readonly ServerPollingSettings _settings;

    public ServerPollingService(
        IServerRepository servers,
        IIncidentRepository incidents,
        IOutboxWriter outbox,
        IUnitOfWork unitOfWork,
        IGoldSrcServerQueryClient queryClient,
        IClock clock,
        ServerPollingSettings settings)
    {
        _servers = servers;
        _incidents = incidents;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
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

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (outcome.OpenedIncident)
            {
                GoldSrcOpsMetrics.RecordAlertEnqueued(IncidentAlertEvents.ServerUnavailable);
            }

            if (outcome.ClosedIncident)
            {
                GoldSrcOpsMetrics.RecordAlertEnqueued(IncidentAlertEvents.ServerRecovered);
            }
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
        GameServerInfo info;

        try
        {
            info = await _queryClient.QueryInfoAsync(
                new GameServerEndpoint(server.Endpoint.Host, server.Endpoint.QueryPort, _settings.QueryTimeout),
                cancellationToken);
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
        var closedIncident = await CloseOpenIncidentAsync(server, checkedAtUtc, cancellationToken);

        return new ServerPollOutcome(Succeeded: true, OpenedIncident: false, ClosedIncident: closedIncident);
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
        _outbox.Add(CreateUnavailableAlert(server, incident));
        return true;
    }

    private async Task<bool> CloseOpenIncidentAsync(
        Server server,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        var openIncident = await _incidents.GetOpenForServerAsync(server.Id, cancellationToken);
        if (openIncident is null)
        {
            return false;
        }

        openIncident.Close(checkedAtUtc, "Server query recovered.");
        _outbox.Add(CreateRecoveredAlert(server, openIncident));
        return true;
    }

    private static IncidentAlertEventV1 CreateUnavailableAlert(
        Server server,
        AvailabilityIncident incident) =>
        new(
            Guid.NewGuid(),
            IncidentAlertEvents.ServerUnavailable,
            incident.OpenedAtUtc,
            incident.Id,
            server.Id,
            server.Name,
            incident.StartReason,
            incident.ConsecutiveFailures,
            incident.OpenedAtUtc,
            ClosedAtUtc: null,
            DurationSeconds: null);

    private static IncidentAlertEventV1 CreateRecoveredAlert(
        Server server,
        AvailabilityIncident incident)
    {
        var closedAtUtc = incident.ClosedAtUtc
            ?? throw new InvalidOperationException("A recovered incident must be closed.");
        var reason = incident.EndReason
            ?? throw new InvalidOperationException("A recovered incident must have an end reason.");
        var durationSeconds = Math.Max(0, (long)(closedAtUtc - incident.OpenedAtUtc).TotalSeconds);

        return new IncidentAlertEventV1(
            Guid.NewGuid(),
            IncidentAlertEvents.ServerRecovered,
            closedAtUtc,
            incident.Id,
            server.Id,
            server.Name,
            reason,
            incident.ConsecutiveFailures,
            incident.OpenedAtUtc,
            closedAtUtc,
            durationSeconds);
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
