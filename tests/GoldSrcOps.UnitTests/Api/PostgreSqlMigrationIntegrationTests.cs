using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlMigrationIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Migrations_can_be_applied_repeatedly_when_role_and_domain_schema_have_same_name()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();

        var historyTableSchemas = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            await dbContext.Database.MigrateAsync();
            await dbContext.Database.OpenConnectionAsync();

            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                SELECT table_schema
                FROM information_schema.tables
                WHERE table_name = '__EFMigrationsHistory'
                ORDER BY table_schema;
                """;

            var schemas = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schemas.Add(reader.GetString(0));
            }

            return schemas;
        });

        historyTableSchemas.Should().Equal("public");
    }
}
