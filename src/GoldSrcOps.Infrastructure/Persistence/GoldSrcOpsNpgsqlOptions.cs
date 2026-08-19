using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GoldSrcOps.Infrastructure.Persistence;

internal static class GoldSrcOpsNpgsqlOptions
{
    private const string MigrationsHistoryTableName = "__EFMigrationsHistory";
    private const string MigrationsHistoryTableSchema = "public";

    public static void Configure(NpgsqlDbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.MigrationsAssembly(typeof(GoldSrcOpsDbContext).Assembly.FullName);
        options.MigrationsHistoryTable(MigrationsHistoryTableName, MigrationsHistoryTableSchema);
    }
}
