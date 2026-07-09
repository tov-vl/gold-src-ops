using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Domain.Servers;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class ServerEndpoints
{
    public static RouteGroupBuilder MapServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/servers").WithTags("Servers");

        group.MapPost("/", RegisterAsync)
            .WithName("RegisterServer");

        group.MapGet("/", ListAsync)
            .WithName("ListServers");

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetServer");

        group.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateServer");

        group.MapPost("/{id:guid}/enable", EnableAsync)
            .WithName("EnableServer");

        group.MapPost("/{id:guid}/disable", DisableAsync)
            .WithName("DisableServer");

        group.MapGet("/{id:guid}/status", GetStatusAsync)
            .WithName("GetServerStatus");

        group.MapGet("/{id:guid}/snapshots", ListSnapshotsAsync)
            .WithName("ListServerSnapshots");

        group.MapGet("/{id:guid}/incidents", ListServerIncidentsAsync)
            .WithName("ListServerIncidents");

        return group;
    }

    private static async Task<Results<Created<ServerResponse>, ValidationProblem>> RegisterAsync(
        RegisterServerRequest request,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var server = await servers.RegisterAsync(
            new RegisterServerCommand(
                request.Name,
                GameServerKind.GoldSrc,
                request.Host,
                request.QueryPort,
                request.RconPort,
                request.PollIntervalSeconds ?? 60,
                request.Notes),
            cancellationToken);

        return TypedResults.Created($"/api/servers/{server.Id}", Map(server));
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateServerRequest request,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var server = await servers.UpdateAsync(
            id,
            new UpdateServerCommand(
                request.Name,
                request.Host,
                request.QueryPort,
                request.RconPort,
                request.PollIntervalSeconds,
                request.Notes),
            cancellationToken);

        return server is null ? TypedResults.NotFound() : TypedResults.Ok(Map(server));
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound>> EnableAsync(
        Guid id,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var server = await servers.EnableAsync(id, cancellationToken);
        return server is null ? TypedResults.NotFound() : TypedResults.Ok(Map(server));
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound>> DisableAsync(
        Guid id,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var server = await servers.DisableAsync(id, cancellationToken);
        return server is null ? TypedResults.NotFound() : TypedResults.Ok(Map(server));
    }

    private static async Task<Ok<IReadOnlyList<ServerResponse>>> ListAsync(
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var result = await servers.ListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<ServerResponse>>(result.Select(Map).ToArray());
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound>> GetAsync(
        Guid id,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(id, cancellationToken);
        return server is null ? TypedResults.NotFound() : TypedResults.Ok(Map(server));
    }

    private static async Task<Results<Ok<ServerStatusResponse>, NotFound>> GetStatusAsync(
        Guid id,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var status = await servers.GetStatusAsync(id, cancellationToken);
        return status is null ? TypedResults.NotFound() : TypedResults.Ok(Map(status));
    }

    private static async Task<Results<Ok<SnapshotHistoryResponse>, NotFound, ValidationProblem>> ListSnapshotsAsync(
        Guid id,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        MonitoringReadService monitoring,
        CancellationToken cancellationToken)
    {
        var errors = ValidateSnapshotQuery(from, to, limit);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await monitoring.ListSnapshotsAsync(id, from, to, limit, cancellationToken);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(Map(result));
    }

    private static async Task<Ok<IReadOnlyList<AvailabilityIncidentResponse>>> ListServerIncidentsAsync(
        Guid id,
        IncidentsService incidents,
        CancellationToken cancellationToken)
    {
        var result = await incidents.ListByServerAsync(id, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<AvailabilityIncidentResponse>>(result.Select(Map).ToArray());
    }

    private static Dictionary<string, string[]> Validate(RegisterServerRequest request)
    {
        var errors = ValidateServerFields(
            request.Name,
            request.Host,
            request.QueryPort,
            request.RconPort);

        if (request.PollIntervalSeconds is <= 0)
        {
            errors[nameof(request.PollIntervalSeconds)] = ["PollIntervalSeconds must be positive."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(UpdateServerRequest request)
    {
        var errors = ValidateServerFields(
            request.Name,
            request.Host,
            request.QueryPort,
            request.RconPort);

        if (request.PollIntervalSeconds <= 0)
        {
            errors[nameof(request.PollIntervalSeconds)] = ["PollIntervalSeconds must be positive."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateServerFields(
        string name,
        string host,
        int queryPort,
        int? rconPort)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["Name"] = ["Server name is required."];
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            errors["Host"] = ["Host is required."];
        }

        if (queryPort is < 1 or > 65535)
        {
            errors["QueryPort"] = ["QueryPort must be between 1 and 65535."];
        }

        if (rconPort is < 1 or > 65535)
        {
            errors["RconPort"] = ["RconPort must be between 1 and 65535."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateSnapshotQuery(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (from is not null && to is not null && from > to)
        {
            errors["from"] = ["From must be earlier than or equal to To."];
        }

        if (limit is <= 0 or > MonitoringReadService.MaxSnapshotLimit)
        {
            errors["limit"] = [$"Limit must be between 1 and {MonitoringReadService.MaxSnapshotLimit}."];
        }

        return errors;
    }

    private static ServerResponse Map(ServerDto server) =>
        new(
            server.Id,
            server.Name,
            server.Game.ToString(),
            server.Host,
            server.QueryPort,
            server.RconPort,
            server.IsEnabled,
            server.PollIntervalSeconds,
            server.Notes,
            server.CreatedAtUtc);

    private static ServerStatusResponse Map(ServerStatusDto status) =>
        new(
            status.ServerId,
            status.Status.ToString(),
            status.IsReachable,
            status.LastCheckedAtUtc,
            status.LastSuccessAtUtc,
            status.LatencyMs,
            status.CurrentMap,
            status.Players,
            status.MaxPlayers,
            status.FailureReason,
            status.ConsecutiveFailures);

    private static SnapshotHistoryResponse Map(SnapshotHistoryDto history) =>
        new(
            history.ServerId,
            history.FromUtc,
            history.ToUtc,
            history.Limit,
            history.Items.Select(Map).ToArray());

    private static PollSnapshotResponse Map(PollSnapshotDto snapshot) =>
        new(
            snapshot.Id,
            snapshot.ServerId,
            snapshot.CheckedAtUtc,
            snapshot.IsReachable,
            snapshot.LatencyMs,
            snapshot.Map,
            snapshot.Players,
            snapshot.MaxPlayers,
            snapshot.Bots,
            snapshot.RawVersion,
            snapshot.FailureReason);

    private static AvailabilityIncidentResponse Map(AvailabilityIncidentDto incident) =>
        new(
            incident.Id,
            incident.ServerId,
            incident.Type.ToString(),
            incident.OpenedAtUtc,
            incident.ClosedAtUtc,
            incident.StartReason,
            incident.EndReason,
            incident.ConsecutiveFailures);
}
