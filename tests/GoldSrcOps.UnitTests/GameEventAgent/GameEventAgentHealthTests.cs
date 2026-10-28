using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventAgentHealthTests
{
    [Fact]
    public void Missing_health_is_unavailable_for_enabled_cycles_and_disabled_for_disabled_cycles()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);

        var result = GameEventAgentHealth.Read(options, GameEventAgentTestData.NowUtc);

        result.Import.State.Should().Be("unavailable");
        result.Delivery.State.Should().Be("disabled");
        Directory.Exists(Path.GetDirectoryName(database.DatabasePath)).Should().BeFalse();
    }

    [Theory]
    [InlineData("idle", "running")]
    [InlineData("progress", "running")]
    [InlineData("failed", "failed")]
    [InlineData("retrying", "retrying")]
    [InlineData("blocked", "blocked")]
    public void Cycle_outcomes_expire_at_the_inclusive_budget_and_recover(string outcome, string expected)
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        new SqliteGameEventOutbox(options.Queue).Initialize();
        var clock = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var health = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        health.RecordStart(import: true);
        GameEventAgentHealth.Read(options, clock.UtcNow).Import.State.Should().Be("starting");
        health.RecordCompletion(import: true, outcome);

        var completed = GameEventAgentHealth.Read(options, clock.UtcNow);
        completed.Import.State.Should().Be(expected);
        completed.Import.Cycle!.LastSuccessfulAtUtc.Should().Be(outcome is "idle" or "progress" ? clock.UtcNow : null);
        clock.UtcNow += TimeSpan.FromSeconds(completed.Import.StaleAfterSeconds);
        GameEventAgentHealth.Read(options, clock.UtcNow).Import.State.Should().Be(expected);
        clock.UtcNow += TimeSpan.FromTicks(1);
        GameEventAgentHealth.Read(options, clock.UtcNow).Import.State.Should().Be("stale");

        health.RecordStart(import: true);
        health.RecordCompletion(import: true, "idle");
        GameEventAgentHealth.Read(options, clock.UtcNow).Import.State.Should().Be("running");
        health.RecordStop(import: true);
        GameEventAgentHealth.Read(options, clock.UtcNow).Import.State.Should().Be("stopped");
    }

    [Fact]
    public void A_new_process_cannot_inherit_previous_success_and_stuck_active_work_expires()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(),
            GameEventAgentTestData.CreateDeliveryOptions(batchSize: 2));
        new SqliteGameEventOutbox(options.Queue).Initialize();
        var clock = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var first = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        first.RecordStart(import: false);
        first.RecordCompletion(import: false, "progress");
        clock.UtcNow += TimeSpan.FromSeconds(1);
        var second = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        second.RecordStart(import: false);

        var current = GameEventAgentHealth.Read(options, clock.UtcNow);
        current.StartedAtUtc.Should().Be(clock.UtcNow);
        current.Delivery.State.Should().Be("starting");
        current.Delivery.Cycle!.LastSuccessfulAtUtc.Should().BeNull();
        clock.UtcNow += TimeSpan.FromSeconds(current.Delivery.StaleAfterSeconds + 1);
        GameEventAgentHealth.Read(options, clock.UtcNow).Delivery.State.Should().Be("stale");
    }

    [Fact]
    public void Invalid_oversized_future_or_incompatible_evidence_is_not_reported_as_live()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        new SqliteGameEventOutbox(options.Queue).Initialize();
        var clock = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var health = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        health.RecordStart(import: true);
        health.RecordCompletion(import: true, "idle");
        var path = GameEventAgentHealth.PathFor(options);
        var original = File.ReadAllText(path);
        foreach (var invalid in new[] { "{", new string('x', 16 * 1024 + 1), original.Replace("idle", "private-untrusted-text", StringComparison.Ordinal) })
        {
            File.WriteAllText(path, invalid);
            var snapshot = GameEventAgentHealth.Read(options, clock.UtcNow);
            snapshot.Import.State.Should().Be("unavailable");
            JsonSerializer.Serialize(snapshot).Should().NotContain("private-untrusted-text");
        }

        File.WriteAllText(path, original);
        GameEventAgentHealth.Read(options, clock.UtcNow.AddSeconds(-1)).Import.State.Should().Be("unavailable");
        GameEventAgentHealth.Read(options with { Delivery = GameEventAgentTestData.CreateDeliveryOptions() }, clock.UtcNow)
            .Delivery.State.Should().Be("unavailable");
    }

    [Fact]
    public void Diagnostic_write_failure_does_not_throw_or_create_queue_directories()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        var health = new GameEventAgentHealth(options, new TestTimeProvider(GameEventAgentTestData.NowUtc),
            NullLogger<GameEventAgentHealth>.Instance);

        var action = () => { health.RecordStart(import: true); health.RecordCompletion(import: true, "idle"); health.RecordStop(import: true); };

        action.Should().NotThrow();
        Directory.Exists(Path.GetDirectoryName(database.DatabasePath)).Should().BeFalse();
    }

    [Fact]
    public void A_preexisting_temporary_record_is_preserved_and_does_not_accumulate_files()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        new SqliteGameEventOutbox(options.Queue).Initialize();
        var path = GameEventAgentHealth.PathFor(options);
        File.WriteAllText(path + ".tmp", "interrupted-record");
        var health = new GameEventAgentHealth(options, new TestTimeProvider(GameEventAgentTestData.NowUtc),
            NullLogger<GameEventAgentHealth>.Instance);

        health.RecordStart(import: true);
        health.RecordCompletion(import: true, "idle");

        File.ReadAllText(path + ".tmp").Should().Be("interrupted-record");
        File.Exists(path).Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Should().ContainSingle();
    }

    [Fact]
    public void Advancing_clock_keeps_completion_and_success_consistent()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        new SqliteGameEventOutbox(options.Queue).Initialize();
        var clock = new AdvancingTimeProvider();
        var health = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);

        health.RecordStart(import: true);
        health.RecordCompletion(import: true, "idle");
        var snapshot = GameEventAgentHealth.Read(options, clock.GetUtcNow());

        snapshot.Import.State.Should().Be("running");
        snapshot.Import.Cycle!.LastCompletedAtUtc.Should().Be(snapshot.Import.Cycle.LastSuccessfulAtUtc);
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = GameEventAgentTestData.NowUtc;

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now = _now.AddTicks(1);
            return now;
        }
    }
}
