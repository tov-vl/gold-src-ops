using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GoldSrcOps.AlertReceiver.Persistence;

public sealed class AlertReceiverDesignTimeDbContextFactory
    : IDesignTimeDbContextFactory<AlertReceiverDbContext>
{
    public AlertReceiverDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AlertReceiverDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=goldsrcops_receiver;Username=postgres",
                npgsql => npgsql.MigrationsHistoryTable(
                    AlertReceiverDbContext.MigrationsHistoryTable,
                    AlertReceiverDbContext.Schema))
            .Options;

        return new AlertReceiverDbContext(options);
    }
}
