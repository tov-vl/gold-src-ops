using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Contracts.Incidents;
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

        group.MapGet("/{id:guid}/status", GetStatusAsync)
            .WithName("GetServerStatus");

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
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors[nameof(request.Name)] = ["Server name is required."];
        }

        if (string.IsNullOrWhiteSpace(request.Host))
        {
            errors[nameof(request.Host)] = ["Host is required."];
        }

        if (request.QueryPort is < 1 or > 65535)
        {
            errors[nameof(request.QueryPort)] = ["QueryPort must be between 1 and 65535."];
        }

        if (request.RconPort is < 1 or > 65535)
        {
            errors[nameof(request.RconPort)] = ["RconPort must be between 1 and 65535."];
        }

        if (request.PollIntervalSeconds is <= 0)
        {
            errors[nameof(request.PollIntervalSeconds)] = ["PollIntervalSeconds must be positive."];
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
