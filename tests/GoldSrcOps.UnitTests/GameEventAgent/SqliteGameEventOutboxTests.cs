using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;
using Microsoft.Data.Sqlite;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class SqliteGameEventOutboxTests
{
    [Fact]
    public async Task Initialize_is_safe_for_concurrent_process_equivalent_instances()
    {
        using var database = new TemporaryAgentDatabase();
        using var ready = new Barrier(participantCount: 8);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                var outbox = new SqliteGameEventOutbox(database.CreateOptions());
                ready.SignalAndWait();
                outbox.Initialize();
                return outbox.GetState();
            }))
            .ToArray();

        var states = await Task.WhenAll(tasks);

        states.Select(static state => state.SourceInstanceId).Distinct().Should().ContainSingle();
        states.Should().AllSatisfy(static state => state.NextSequenceNumber.Should().Be(1));
    }

    [Fact]
    public void Enqueue_preserves_source_identity_and_monotonic_sequence_across_reopen()
    {
        using var database = new TemporaryAgentDatabase();
        var firstOutbox = new SqliteGameEventOutbox(database.CreateOptions());
        firstOutbox.Initialize();

        var first = firstOutbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var reopenedOutbox = new SqliteGameEventOutbox(database.CreateOptions());
        reopenedOutbox.Initialize();
        var second = reopenedOutbox.Enqueue(
            GameEventAgentTestData.CreateInput() with
            {
                Type = "server.stopped",
                Map = null,
                Players = null,
                Bots = null
            },
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1));

        second.SourceInstanceId.Should().Be(first.SourceInstanceId);
        first.SequenceNumber.Should().Be(1);
        second.SequenceNumber.Should().Be(2);
        reopenedOutbox.GetState().NextSequenceNumber.Should().Be(3);
        reopenedOutbox.GetStatistics().Pending.Should().Be(2);
    }

    [Fact]
    public void Claim_reuses_identical_payload_after_an_expired_lease()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var queued = outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var firstClaim = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc,
            TimeSpan.FromSeconds(30));
        var prematureClaim = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(30));
        var recoveredClaim = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        firstClaim.Should().NotBeNull();
        prematureClaim.Should().BeNull();
        recoveredClaim.Should().NotBeNull();
        recoveredClaim!.EventId.Should().Be(queued.EventId);
        recoveredClaim.SequenceNumber.Should().Be(queued.SequenceNumber);
        recoveredClaim.AttemptCount.Should().Be(2);
        recoveredClaim.PayloadUtf8.Should().Equal(queued.PayloadUtf8);
    }

    [Fact]
    public void Enqueue_is_atomic_when_capacity_is_reached()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions(capacity: 1));
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var act = () => outbox.Enqueue(
            GameEventAgentTestData.CreateInput(),
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1));

        act.Should().Throw<GameEventQueueFullException>();
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetState().NextSequenceNumber.Should().Be(2);
    }

    [Fact]
    public void Enqueue_stores_a_bounded_versioned_request_with_no_extension_fields()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());

        var queued = outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        queued.PayloadUtf8.Should().HaveCountLessThanOrEqualTo(GameEventJson.MaximumRequestBytes);
        using var document = JsonDocument.Parse(queued.PayloadUtf8);
        document.RootElement.GetProperty("contractVersion").GetInt16().Should().Be(1);
        document.RootElement.GetProperty("eventId").GetGuid().Should().Be(queued.EventId);
        document.RootElement.GetProperty("sourceInstanceId").GetGuid().Should().Be(queued.SourceInstanceId);
        document.RootElement.GetProperty("sequenceNumber").GetInt64().Should().Be(1);
        document.RootElement.EnumerateObject().Should().HaveCount(9);
    }

    [Fact]
    public void EnqueueFromSpool_reconciles_identical_content_without_advancing_sequence()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var recordId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        var contentHash = SHA256.HashData("same-record"u8);

        var first = outbox.EnqueueFromSpool(
            recordId,
            contentHash,
            GameEventAgentTestData.CreateInput(),
            GameEventAgentTestData.NowUtc);
        var duplicate = outbox.EnqueueFromSpool(
            recordId,
            contentHash,
            GameEventAgentTestData.CreateInput(),
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1));

        first.AlreadyQueued.Should().BeFalse();
        duplicate.AlreadyQueued.Should().BeTrue();
        duplicate.EventId.Should().Be(first.EventId);
        duplicate.SequenceNumber.Should().Be(first.SequenceNumber);
        outbox.GetStatistics().Should().BeEquivalentTo(new
        {
            Pending = 1,
            SpoolReceipts = 1,
            NextSequenceNumber = 2L
        });

        outbox.CompleteSpoolRecord(recordId, contentHash);
        outbox.CompleteSpoolRecord(recordId, contentHash);
        outbox.GetStatistics().SpoolReceipts.Should().Be(0);
    }

    [Fact]
    public void EnqueueFromSpool_rejects_record_id_reuse_with_different_content()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var recordId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
        outbox.EnqueueFromSpool(
            recordId,
            SHA256.HashData("first-record"u8),
            GameEventAgentTestData.CreateInput(),
            GameEventAgentTestData.NowUtc);

        var act = () => outbox.EnqueueFromSpool(
            recordId,
            SHA256.HashData("changed-record"u8),
            GameEventAgentTestData.CreateInput(),
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1));

        act.Should().Throw<GameEventSpoolRecordConflictException>();
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetStatistics().SpoolReceipts.Should().Be(1);
        outbox.GetState().NextSequenceNumber.Should().Be(2);
    }

    [Fact]
    public void Initialize_migrates_a_version_one_queue_without_replacing_its_identity()
    {
        using var database = new TemporaryAgentDatabase();
        var sourceInstanceId = Guid.Parse("99999999-8888-4777-8666-555555555555");
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE agent_state (
                    singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                    source_instance_id TEXT NOT NULL,
                    next_sequence_number INTEGER NOT NULL CHECK (next_sequence_number > 0)
                );
                CREATE TABLE game_event_outbox (
                    event_id TEXT NOT NULL PRIMARY KEY,
                    source_instance_id TEXT NOT NULL,
                    sequence_number INTEGER NOT NULL UNIQUE CHECK (sequence_number > 0),
                    contract_version INTEGER NOT NULL CHECK (contract_version > 0),
                    event_type TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    map TEXT NULL,
                    players INTEGER NULL,
                    bots INTEGER NULL,
                    payload_json BLOB NOT NULL CHECK (length(payload_json) <= 4096),
                    created_at_utc TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                    next_attempt_at_utc TEXT NOT NULL,
                    lease_until_utc TEXT NULL,
                    last_failure_code TEXT NULL,
                    CHECK ((players IS NULL AND bots IS NULL) OR (players IS NOT NULL AND bots IS NOT NULL)),
                    CHECK (status IN (0, 1, 2))
                );
                INSERT INTO agent_state(singleton_id, source_instance_id, next_sequence_number)
                VALUES (1, $source_instance_id, 7);
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$source_instance_id", sourceInstanceId.ToString("D"));
            command.ExecuteNonQuery();
        }

        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.Initialize();

        outbox.GetState().Should().Be(new GameEventQueueState(sourceInstanceId, 7));
        outbox.GetStatistics().SpoolReceipts.Should().Be(0);
        using var verificationConnection = new SqliteConnection(connectionString);
        verificationConnection.Open();
        using var versionCommand = verificationConnection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture).Should().Be(2);
    }
}
