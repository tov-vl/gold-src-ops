using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Domain.Servers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace GoldSrcOps.Api.Endpoints;

public static class ServerEndpoints
{
    private const string RegistrationIdempotencyConflictCode =
        "server_registration.idempotency_conflict";
    private const string MonitoringMustBePausedCode =
        "server_update.monitoring_must_be_paused";
    private const string ServerRevisionConflictCode =
        "server_update.revision_conflict";

    public static RouteGroupBuilder MapServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/servers").WithTags("Servers");

        group.MapPost("/", RegisterAsync)
            .WithName("RegisterServer")
            .RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy);

        group.MapGet("/", ListAsync)
            .WithName("ListServers")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetServer")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateServer")
            .RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy);

        group.MapPost("/{id:guid}/enable", EnableAsync)
            .WithName("EnableServer")
            .RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy);

        group.MapPost("/{id:guid}/disable", DisableAsync)
            .WithName("DisableServer")
            .RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy);

        group.MapGet("/{id:guid}/status", GetStatusAsync)
            .WithName("GetServerStatus")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/{id:guid}/snapshots", ListSnapshotsAsync)
            .WithName("ListServerSnapshots")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/{id:guid}/incidents", ListServerIncidentsAsync)
            .WithName("ListServerIncidents")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        return group;
    }

    private static async Task<Results<Created<ServerResponse>, Ok<ServerResponse>, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        RegisterServerRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        Guid? registrationRequestId = null;
        if (idempotencyKey is not null)
        {
            if (!Guid.TryParseExact(idempotencyKey, "D", out var parsedRequestId) ||
                parsedRequestId == Guid.Empty)
            {
                errors["Idempotency-Key"] =
                    ["Idempotency-Key must be a non-empty UUID in canonical form."];
            }
            else
            {
                registrationRequestId = parsedRequestId;
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await servers.RegisterAsync(
            new RegisterServerCommand(
                request.Name,
                GameServerKind.GoldSrc,
                request.Host,
                request.QueryPort,
                request.RconPort,
                request.PollIntervalSeconds ?? 60,
                request.Notes,
                request.IsEnabled ?? true,
                registrationRequestId),
            cancellationToken);

        if (result.Kind == RegisterServerResultKind.IdempotencyConflict)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The idempotency key was already used for a different server registration.",
                extensions:
                [
                    new KeyValuePair<string, object?>(
                        "code",
                        RegistrationIdempotencyConflictCode)
                ]);
        }

        var server = result.Server ?? throw new InvalidOperationException(
            "A successful registration result must contain its server.");
        var response = Map(server);

        return result.Kind == RegisterServerResultKind.Created
            ? TypedResults.Created($"/api/servers/{server.Id}", response)
            : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAsync(
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

        var result = await servers.UpdateAsync(
            id,
            new UpdateServerCommand(
                request.ExpectedRevision,
                request.Name,
                request.Host,
                request.QueryPort,
                request.RconPort,
                request.PollIntervalSeconds,
                request.Notes),
            cancellationToken);

        return result.Kind switch
        {
            UpdateServerResultKind.Updated => TypedResults.Ok(Map(
                result.Server ?? throw new InvalidOperationException(
                    "A successful server update must return the server."))),
            UpdateServerResultKind.NotFound => TypedResults.NotFound(),
            UpdateServerResultKind.MonitoringEnabled => Conflict(
                "Pause monitoring before editing server configuration.",
                MonitoringMustBePausedCode),
            UpdateServerResultKind.RevisionConflict => Conflict(
                "The server changed after this configuration was loaded.",
                ServerRevisionConflictCode),
            _ => throw new InvalidOperationException(
                $"Unsupported server update result '{result.Kind}'.")
        };
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound, ProblemHttpResult>> EnableAsync(
        Guid id,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var result = await servers.EnableAsync(id, cancellationToken);
        return MapEnabledResult(result);
    }

    private static async Task<Results<Ok<ServerResponse>, NotFound, ProblemHttpResult>> DisableAsync(
        Guid id,
        ServersService servers,
        CancellationToken cancellationToken)
    {
        var result = await servers.DisableAsync(id, cancellationToken);
        return MapEnabledResult(result);
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

    private static async Task<Results<Ok<IReadOnlyList<AvailabilityIncidentResponse>>, ValidationProblem>>
        ListServerIncidentsAsync(
            Guid id,
            int? limit,
            IncidentsService incidents,
            CancellationToken cancellationToken)
    {
        var errors = ValidateIncidentQuery(limit);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await incidents.ListByServerAsync(id, limit, cancellationToken);
        return TypedResults.Ok<IReadOnlyList<AvailabilityIncidentResponse>>(result.Select(Map).ToArray());
    }

    private static Dictionary<string, string[]> Validate(RegisterServerRequest request)
    {
        var errors = ValidateServerFields(
            request.Name,
            request.Host,
            request.QueryPort,
            request.RconPort,
            request.Notes);

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
            request.RconPort,
            request.Notes);

        if (request.PollIntervalSeconds <= 0)
        {
            errors[nameof(request.PollIntervalSeconds)] = ["PollIntervalSeconds must be positive."];
        }

        if (request.ExpectedRevision <= 0)
        {
            errors[nameof(request.ExpectedRevision)] = ["ExpectedRevision must be positive."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateServerFields(
        string name,
        string host,
        int queryPort,
        int? rconPort,
        string? notes)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["Name"] = ["Server name is required."];
        }
        else if (name.Trim().Length > Server.MaxNameLength)
        {
            errors["Name"] = [$"Server name must not exceed {Server.MaxNameLength} characters."];
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            errors["Host"] = ["Host is required."];
        }
        else if (host.Trim().Length > ServerEndpoint.MaxHostLength)
        {
            errors["Host"] = [$"Host must not exceed {ServerEndpoint.MaxHostLength} characters."];
        }

        if (queryPort is < 1 or > 65535)
        {
            errors["QueryPort"] = ["QueryPort must be between 1 and 65535."];
        }

        if (rconPort is < 1 or > 65535)
        {
            errors["RconPort"] = ["RconPort must be between 1 and 65535."];
        }

        if (notes?.Trim().Length > Server.MaxNotesLength)
        {
            errors["Notes"] = [$"Notes must not exceed {Server.MaxNotesLength} characters."];
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

    private static Dictionary<string, string[]> ValidateIncidentQuery(int? limit)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (limit is <= 0 or > IncidentsService.MaxIncidentHistoryLimit)
        {
            errors["limit"] = [$"Limit must be between 1 and {IncidentsService.MaxIncidentHistoryLimit}."];
        }

        return errors;
    }

    private static ServerResponse Map(ServerDto server) =>
        new(
            server.Id,
            server.Revision,
            server.Name,
            server.Game.ToString(),
            server.Host,
            server.QueryPort,
            server.RconPort,
            server.IsEnabled,
            server.PollIntervalSeconds,
            server.Notes,
            server.CreatedAtUtc);

    private static Results<Ok<ServerResponse>, NotFound, ProblemHttpResult> MapEnabledResult(
        SetServerEnabledResult result) => result.Kind switch
        {
            SetServerEnabledResultKind.Updated => TypedResults.Ok(Map(
                result.Server ?? throw new InvalidOperationException(
                    "A successful monitoring update must return the server."))),
            SetServerEnabledResultKind.NotFound => TypedResults.NotFound(),
            SetServerEnabledResultKind.RevisionConflict => Conflict(
                "The monitoring state changed concurrently. Reload it before trying again.",
                ServerRevisionConflictCode),
            _ => throw new InvalidOperationException(
                $"Unsupported monitoring update result '{result.Kind}'.")
        };

    private static ProblemHttpResult Conflict(string title, string code) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: title,
            extensions:
            [
                new KeyValuePair<string, object?>("code", code)
            ]);

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
