using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Infrastructure.Persistence;

internal sealed class EfUnitOfWork(GoldSrcOpsDbContext dbContext) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
