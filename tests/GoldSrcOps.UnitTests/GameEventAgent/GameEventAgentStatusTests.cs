using System.Security.Cryptography;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;
using Microsoft.Data.Sqlite;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventAgentStatusTests
{
    [Fact]
    public void Capture_returns_a_bounded_identity_free_aggregate_snapshot()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var queueOptions = database.CreateOptions(capacity: 250);
        var spoolOptions = spool.CreateOptions(enabled: true);
        GameEventSpoolFileSystem.EnsureDirectories(spoolOptions);
        File.WriteAllText(Path.Combine(spoolOptions.IncomingPath, "ready.json"), "{}");
        File.WriteAllText(Path.Combine(spoolOptions.IncomingPath, "writing.tmp"), "{}");
        File.WriteAllText(Path.Combine(spoolOptions.RejectedPath, "rejected.json"), "{}");

        var outbox = new SqliteGameEventOutbox(queueOptions);
        outbox.Initialize();
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var snapshot = GameEventAgentStatus.Capture(
            new GameEventAgentOptions(queueOptions, spoolOptions, Delivery: null));
        var json = JsonSerializer.Serialize(snapshot, GameEventJson.SerializerOptions);

        snapshot.SchemaVersion.Should().Be(GameEventAgentStatus.SchemaVersion);
        snapshot.SpoolImportEnabled.Should().BeTrue();
        snapshot.DeliveryEnabled.Should().BeFalse();
        snapshot.QueueCapacity.Should().Be(250);
        snapshot.Queue.Should().Be(new GameEventQueueStatistics(1, 0, 0, 0, 2));
        snapshot.Spool.Should().Be(new GameEventSpoolStatistics(1, 0, 0, 1, 1));
        json.Should().Contain("\"schemaVersion\":1");
        json.Should().Contain("\"queueCapacity\":250");
        json.Should().NotContain("sourceInstanceId");
        json.Should().NotContain("de_dust2");
    }

    [Fact]
    public void Capture_does_not_create_a_missing_queue_or_spool()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);

        var action = () => GameEventAgentStatus.Capture(options);

        action.Should().Throw<InvalidOperationException>().WithMessage("*queue is missing*");
        Directory.Exists(Path.GetDirectoryName(database.DatabasePath)).Should().BeFalse();
        Directory.Exists(options.Spool.RootPath).Should().BeFalse();
    }

    [Fact]
    public void Capture_reports_waiting_age_and_preserves_database_bytes_state_and_missing_spool()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        var outbox = new SqliteGameEventOutbox(options.Queue);
        outbox.Initialize();
        var now = GameEventAgentTestData.NowUtc;
        var dead = outbox.Enqueue(GameEventAgentTestData.CreateInput(), now.AddSeconds(-100));
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), now.AddSeconds(-40));
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), now.AddSeconds(-10));
        outbox.ClaimNext(now, TimeSpan.FromMinutes(2));
        outbox.MoveToDeadLetter(dead.EventId, GameEventDeliveryFailure.Expired);
        outbox.ClaimNext(now, TimeSpan.FromMinutes(2));
        var before = SHA256.HashData(File.ReadAllBytes(database.DatabasePath));
        var state = outbox.GetState();

        var result = GameEventAgentStatus.Capture(options, new TestTimeProvider(now));

        result.ObservedAtUtc.Should().Be(now);
        result.Queue.Pending.Should().Be(1);
        result.Queue.InFlight.Should().Be(1);
        result.Queue.DeadLetter.Should().Be(1);
        result.OldestWaitingAtUtc.Should().Be(now.AddSeconds(-40));
        result.OldestWaitingAgeSeconds.Should().Be(40);
        result.SpoolAvailable.Should().BeFalse();
        Directory.Exists(options.Spool.RootPath).Should().BeFalse();
        SHA256.HashData(File.ReadAllBytes(database.DatabasePath)).Should().Equal(before);
        outbox.GetState().Should().Be(state);
    }

    [Fact]
    public void Capture_has_no_age_for_an_empty_queue_and_does_not_migrate_an_old_schema()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(), Delivery: null);
        new SqliteGameEventOutbox(options.Queue).Initialize();
        var empty = GameEventAgentStatus.Capture(options);
        empty.OldestWaitingAtUtc.Should().BeNull();
        empty.OldestWaitingAgeSeconds.Should().BeNull();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=1;";
        command.ExecuteNonQuery();

        var action = () => GameEventAgentStatus.Capture(options);

        action.Should().Throw<InvalidOperationException>().WithMessage("*does not migrate*");
        command.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
    }
}
