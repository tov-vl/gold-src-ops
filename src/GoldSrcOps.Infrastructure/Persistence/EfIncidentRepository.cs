using GoldSrcOps.Application.Incidents;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfIncidentRepository : IIncidentRepository
{
    private readonly GoldSrcOpsDbContext _dbContext;

    public EfIncidentRepository(GoldSrcOpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AvailabilityIncident?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.AvailabilityIncidents
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<AvailabilityIncident?> GetOpenForServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.AvailabilityIncidents
            .FirstOrDefaultAsync(x => x.ServerId == serverId && x.ClosedAtUtc == null, cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilityIncident>> ListOpenAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.AvailabilityIncidents
            .AsNoTracking()
            .Where(x => x.ClosedAtUtc == null)
            .OrderByDescending(x => x.OpenedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvailabilityIncident>> ListByServerAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.AvailabilityIncidents
            .AsNoTracking()
            .Where(x => x.ServerId == serverId)
            .OrderByDescending(x => x.OpenedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(AvailabilityIncident incident, CancellationToken cancellationToken)
    {
        await _dbContext.AvailabilityIncidents.AddAsync(incident, cancellationToken);
    }
}
