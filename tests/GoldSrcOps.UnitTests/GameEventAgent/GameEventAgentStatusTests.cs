using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

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
}
