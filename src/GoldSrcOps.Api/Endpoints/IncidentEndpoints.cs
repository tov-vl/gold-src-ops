using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Contracts.Incidents;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class IncidentEndpoints
{
    public static RouteGroupBuilder MapIncidentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/incidents")
            .WithTags("Incidents")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/open", ListOpenAsync)
            .WithName("ListOpenIncidents");

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetIncident");

        return group;
    }

    private static async Task<Ok<IReadOnlyList<AvailabilityIncidentResponse>>> ListOpenAsync(
        IncidentsService incidents,
        CancellationToken cancellationToken)
    {
        var result = await incidents.ListOpenAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<AvailabilityIncidentResponse>>(result.Select(Map).ToArray());
    }

    private static async Task<Results<Ok<AvailabilityIncidentResponse>, NotFound>> GetAsync(
        Guid id,
        IncidentsService incidents,
        CancellationToken cancellationToken)
    {
        var incident = await incidents.GetAsync(id, cancellationToken);
        return incident is null ? TypedResults.NotFound() : TypedResults.Ok(Map(incident));
    }

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
