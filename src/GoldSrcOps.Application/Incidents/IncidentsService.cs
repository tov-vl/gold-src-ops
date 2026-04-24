using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Incidents;

public sealed class IncidentsService
{
    private readonly IIncidentRepository _incidents;

    public IncidentsService(IIncidentRepository incidents)
    {
        _incidents = incidents;
    }

    public async Task<AvailabilityIncidentDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var incident = await _incidents.GetAsync(id, cancellationToken);
        return incident is null ? null : Map(incident);
    }

    public async Task<IReadOnlyList<AvailabilityIncidentDto>> ListOpenAsync(CancellationToken cancellationToken)
    {
        var incidents = await _incidents.ListOpenAsync(cancellationToken);
        return incidents.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<AvailabilityIncidentDto>> ListByServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var incidents = await _incidents.ListByServerAsync(serverId, cancellationToken);
        return incidents.Select(Map).ToArray();
    }

    private static AvailabilityIncidentDto Map(AvailabilityIncident incident) =>
        new(
            incident.Id,
            incident.ServerId,
            incident.Type,
            incident.OpenedAtUtc,
            incident.ClosedAtUtc,
            incident.StartReason,
            incident.EndReason,
            incident.ConsecutiveFailures);
}
