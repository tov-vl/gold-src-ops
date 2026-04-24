using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Incidents;

public interface IIncidentRepository
{
    Task<AvailabilityIncident?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<AvailabilityIncident?> GetOpenForServerAsync(Guid serverId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AvailabilityIncident>> ListOpenAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AvailabilityIncident>> ListByServerAsync(Guid serverId, CancellationToken cancellationToken);

    Task AddAsync(AvailabilityIncident incident, CancellationToken cancellationToken);
}
