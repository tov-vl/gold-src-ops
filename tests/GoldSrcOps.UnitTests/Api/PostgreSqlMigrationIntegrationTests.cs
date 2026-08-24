using System.Data.Common;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Outbox_migration_creates_operational_schema_and_enforces_invariants()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();

        var schema = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            await dbContext.Database.OpenConnectionAsync();
            var connection = dbContext.Database.GetDbConnection();

            return new OutboxSchema(
                await ReadSingleColumnAsync(
                    connection,
                    """
                    SELECT
                        column_name || ':' ||
                        data_type || ':' ||
                        is_nullable || ':' ||
                        COALESCE(character_maximum_length::text, '-')
                    FROM information_schema.columns
                    WHERE table_schema = 'goldsrcops'
                      AND table_name = 'outbox_messages'
                    ORDER BY ordinal_position;
                    """),
                await ReadPairsAsync(
                    connection,
                    """
                    SELECT indexname, indexdef
                    FROM pg_indexes
                    WHERE schemaname = 'goldsrcops'
                      AND tablename = 'outbox_messages'
                    ORDER BY indexname;
                    """),
                await ReadPairsAsync(
                    connection,
                    """
                    SELECT conname, pg_get_constraintdef(oid)
                    FROM pg_constraint
                    WHERE conrelid = 'goldsrcops.outbox_messages'::regclass
                    ORDER BY conname;
                    """));
        });

        schema.Columns.Should().Equal(
            "Id:uuid:NO:-",
            "EventType:character varying:NO:128",
            "PayloadVersion:smallint:NO:-",
            "AggregateType:character varying:NO:64",
            "AggregateId:uuid:NO:-",
            "OccurredAtUtc:timestamp with time zone:NO:-",
            "Payload:jsonb:NO:-",
            "Status:character varying:NO:32",
            "AttemptCount:integer:NO:-",
            "NextAttemptAtUtc:timestamp with time zone:NO:-",
            "ClaimId:uuid:YES:-",
            "ClaimedAtUtc:timestamp with time zone:YES:-",
            "ProcessedAtUtc:timestamp with time zone:YES:-",
            "LastError:character varying:YES:2000");

        Assert.Contains("IX_outbox_messages_pending_claim", schema.Indexes.Keys);
        Assert.Contains("IX_outbox_messages_processed_cleanup", schema.Indexes.Keys);
        Assert.Contains("IX_outbox_messages_processing_recovery", schema.Indexes.Keys);
        Assert.Contains("IX_outbox_messages_active_aggregate_order", schema.Indexes.Keys);
        Assert.Contains("UX_outbox_messages_EventType_AggregateId", schema.Indexes.Keys);
        schema.Indexes["IX_outbox_messages_active_aggregate_order"].Should().Contain("Pending");
        schema.Indexes["IX_outbox_messages_active_aggregate_order"].Should().Contain("Processing");
        schema.Indexes["IX_outbox_messages_pending_claim"].Should().Contain("Pending");
        schema.Indexes["IX_outbox_messages_processed_cleanup"].Should().Contain("Processed");
        schema.Indexes["IX_outbox_messages_processing_recovery"].Should().Contain("Processing");
        Assert.Contains("CK_outbox_messages_AttemptCount", schema.Constraints.Keys);
        Assert.Contains("CK_outbox_messages_PayloadVersion", schema.Constraints.Keys);
        Assert.Contains("CK_outbox_messages_StatusFields", schema.Constraints.Keys);

        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var aggregateId = Guid.NewGuid();
            var stateChangedAtUtc = new DateTimeOffset(2026, 8, 24, 12, 1, 0, TimeSpan.Zero);
            await InsertOutboxMessageAsync(dbContext, Guid.NewGuid(), aggregateId);
            await InsertOutboxMessageAsync(
                dbContext,
                Guid.NewGuid(),
                Guid.NewGuid(),
                attemptCount: 1,
                status: "Processing",
                claimId: Guid.NewGuid(),
                claimedAtUtc: stateChangedAtUtc);
            await InsertOutboxMessageAsync(
                dbContext,
                Guid.NewGuid(),
                Guid.NewGuid(),
                attemptCount: 1,
                status: "Processed",
                processedAtUtc: stateChangedAtUtc);
            await InsertOutboxMessageAsync(
                dbContext,
                Guid.NewGuid(),
                Guid.NewGuid(),
                attemptCount: 1,
                status: "DeadLetter");

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertOutboxMessageAsync(dbContext, Guid.NewGuid(), aggregateId));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
            Assert.Equal("UX_outbox_messages_EventType_AggregateId", duplicate.ConstraintName);

            var invalidVersion = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertOutboxMessageAsync(
                    dbContext,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    payloadVersion: 0));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidVersion.SqlState);
            Assert.Equal("CK_outbox_messages_PayloadVersion", invalidVersion.ConstraintName);

            var invalidAttemptCount = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertOutboxMessageAsync(
                    dbContext,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    attemptCount: -1));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidAttemptCount.SqlState);
            Assert.Equal("CK_outbox_messages_AttemptCount", invalidAttemptCount.ConstraintName);

            var invalidClaim = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertOutboxMessageAsync(
                    dbContext,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    status: "Processing"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidClaim.SqlState);
            Assert.Equal("CK_outbox_messages_StatusFields", invalidClaim.ConstraintName);
        });
    }

    private static async Task<List<string>> ReadSingleColumnAsync(
        DbConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<Dictionary<string, string>> ReadPairsAsync(
        DbConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0), reader.GetString(1));
        }

        return values;
    }

    private static Task<int> InsertOutboxMessageAsync(
        GoldSrcOpsDbContext dbContext,
        Guid messageId,
        Guid aggregateId,
        short payloadVersion = 1,
        int attemptCount = 0,
        string status = "Pending",
        Guid? claimId = null,
        DateTimeOffset? claimedAtUtc = null,
        DateTimeOffset? processedAtUtc = null)
    {
        var occurredAtUtc = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var payload = $$"""{"eventId":"{{messageId}}"}""";

        return dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO goldsrcops.outbox_messages
                ("Id", "EventType", "PayloadVersion", "AggregateType", "AggregateId",
                 "OccurredAtUtc", "Payload", "Status", "AttemptCount", "NextAttemptAtUtc",
                 "ClaimId", "ClaimedAtUtc", "ProcessedAtUtc")
            VALUES
                ({{messageId}}, 'server.availability.unavailable', {{payloadVersion}},
                 'availability-incident', {{aggregateId}}, {{occurredAtUtc}},
                 CAST({{payload}} AS jsonb), {{status}}, {{attemptCount}}, {{occurredAtUtc}},
                 {{claimId}}, {{claimedAtUtc}}, {{processedAtUtc}});
            """);
    }

    private sealed record OutboxSchema(
        List<string> Columns,
        Dictionary<string, string> Indexes,
        Dictionary<string, string> Constraints);
}
