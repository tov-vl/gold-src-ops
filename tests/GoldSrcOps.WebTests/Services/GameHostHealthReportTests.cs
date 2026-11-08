using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class GameHostHealthReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fresh_report_contains_five_cards_ages_zero_and_static_warning_guidance()
    {
        var snapshot = Snapshot() with
        {
            Values = [new GameHostHealthValueResponse("swapUsedBytes", string.Empty, 0), new GameHostHealthValueResponse("backupSource", string.Empty, 2), new GameHostHealthValueResponse("weeklyAttempt", string.Empty, 9)],
            Warnings = new GameHostWarningsResponse("Available", Now.AddSeconds(-10),
                [new GameHostWarningResponse("PlayerBackupInterventionRequired", "private-component-sentinel", "Firing", "private-description-sentinel")])
        };
        var text = GameHostHealthReport.Create(snapshot, Now);
        text.Should().Contain("Generated: 2026-11-08 12:00:00 UTC").And.Contain("Snapshot age (seconds): 5");
        foreach (var label in new[] { "Agent / queue", "Player backup", "Disk / inode", "RAM / swap", "Sampled CPU" })
        {
            text.Should().Contain(label + ":");
        }

        text.Should().Contain("age (seconds): 30").And.Contain("Swap used (bytes): 0")
            .And.Contain("Backup copy source: Manual").And.Contain("Last weekly attempt: Closed: outcome uncertain")
            .And.Contain("PlayerBackupInterventionRequired: Firing").And.Contain("Do not retry an uncertain backup")
            .And.Contain("Disk available (bytes) (queue): Unknown").And.NotContain("private-");
    }

    [Fact]
    public void Missing_snapshot_is_unknown_and_cannot_claim_no_warnings()
    {
        var text = GameHostHealthReport.Create(snapshot: null, generatedAt: Now);
        text.Should().Contain("Snapshot: Unknown").And.Contain("Player backup: Unknown")
            .And.Contain("Warning snapshot: Unknown").And.Contain("#unknown-snapshot")
            .And.NotContain("No pending or firing").And.NotContain("No warnings");
    }

    [Theory]
    [InlineData(-181, "Stale")]
    [InlineData(1, "Unknown")]
    public void Old_or_future_whole_snapshot_cannot_claim_current_health(int seconds, string state)
    {
        var snapshot = Snapshot() with { EvaluatedAtUtc = Now.AddSeconds(seconds), Values = [new GameHostHealthValueResponse("queuePending", string.Empty, 123)] };
        var text = GameHostHealthReport.Create(snapshot, Now);
        text.Should().Contain("Snapshot: " + state).And.Contain("Agent / queue: " + state)
            .And.Contain("Queue pending: Unknown").And.NotContain("No pending or firing");
    }

    [Theory]
    [InlineData(-180, "No warnings")]
    [InlineData(-181, "Stale")]
    [InlineData(1, "Unknown")]
    public void Component_age_uses_existing_three_minute_boundary(int seconds, string state)
    {
        var snapshot = Snapshot() with { Components = [new GameHostHealthComponentResponse("agent", "NoWarnings", Now.AddSeconds(seconds))] };
        GameHostHealthReport.Create(snapshot, Now).Should().Contain("Agent / queue: " + state).And.Contain("RAM / swap: Unknown");
    }

    [Theory]
    [InlineData("Available", -60, "Available", true)]
    [InlineData("Available", -61, "Stale", false)]
    [InlineData("Available", 1, "Unknown", false)]
    [InlineData("Stale", -10, "Stale", false)]
    [InlineData("Unknown", -10, "Unknown", false)]
    public void Empty_warning_list_requires_fresh_available_rule_snapshot(string sourceState, int age, string state, bool empty)
    {
        var snapshot = Snapshot() with { Warnings = new GameHostWarningsResponse(sourceState, Now.AddSeconds(age), []) };
        var text = GameHostHealthReport.Create(snapshot, Now);
        text.Should().Contain("Warning snapshot: " + state);
        text.Contains("No pending or firing", StringComparison.Ordinal).Should().Be(empty);
    }

    [Fact]
    public void Malformed_fields_never_reflect_provider_text_and_oversized_collections_are_bounded()
    {
        const string unsafeText = "private-sentinel\r\nAuthorization: secret\e[31m";
        var snapshot = Snapshot() with
        {
            Components = [.. Enumerable.Repeat(new GameHostHealthComponentResponse(unsafeText, unsafeText, Now), 100)],
            Values = [.. Enumerable.Repeat(new GameHostHealthValueResponse(unsafeText, unsafeText, double.MaxValue), 100)],
            Warnings = new GameHostWarningsResponse(unsafeText, Now, [new GameHostWarningResponse(unsafeText, unsafeText, unsafeText, unsafeText)])
        };
        var text = GameHostHealthReport.Create(snapshot, Now);
        text.Should().NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("secret").And.NotContain("\e");
        text.Should().Contain("Agent / queue: Unknown").And.Contain("Warning snapshot: Unknown");
        Encoding.UTF8.GetByteCount(text).Should().BeLessThan(32768);
    }

    [Fact]
    public void Duplicate_components_values_and_warnings_remain_unknown()
    {
        var snapshot = Snapshot() with
        {
            Components = [new GameHostHealthComponentResponse("agent", "Firing", Now), new GameHostHealthComponentResponse("agent", "NoWarnings", Now)],
            Values = [new GameHostHealthValueResponse("queuePending", string.Empty, 0), new GameHostHealthValueResponse("queuePending", string.Empty, 1)],
            Warnings = new GameHostWarningsResponse("Available", Now,
                [new GameHostWarningResponse("GameHostMemoryLow", "game-host-resources", "Firing", ""), new GameHostWarningResponse("GameHostMemoryLow", "game-host-resources", "Pending", "")])
        };
        GameHostHealthReport.Create(snapshot, Now).Should().Contain("Agent / queue: Unknown")
            .And.Contain("Queue pending: Unknown").And.Contain("Warning snapshot: Unknown").And.NotContain("No pending or firing");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void Invalid_numeric_measurements_are_unknown(double value)
    {
        var snapshot = Snapshot() with { Values = [new GameHostHealthValueResponse("queuePending", string.Empty, value)] };
        GameHostHealthReport.Create(snapshot, Now).Should().Contain("Queue pending: Unknown");
    }

    [Fact]
    public void Null_collections_from_a_malformed_response_do_not_become_empty_healthy_data()
    {
        var snapshot = Snapshot() with { Components = null!, Values = null!, Warnings = new GameHostWarningsResponse("Available", Now, null!) };
        GameHostHealthReport.Create(snapshot, Now).Should().Contain("Agent / queue: Unknown")
            .And.Contain("Queue pending: Unknown").And.Contain("Warning snapshot: Unknown").And.NotContain("No pending or firing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-name\r\nprivate-sentinel")]
    public void Invalid_warning_names_fail_closed_without_reflection(string? name)
    {
        var snapshot = Snapshot() with { Warnings = new GameHostWarningsResponse("Available", Now, [new GameHostWarningResponse(name!, "", "Firing", "private-sentinel")]) };
        GameHostHealthReport.Create(snapshot, Now).Should().Contain("Warning snapshot: Unknown").And.NotContain("private-sentinel");
    }

    private static GameHostHealthResponse Snapshot() => new(Now.AddSeconds(-5),
        [new GameHostHealthComponentResponse("agent", "NoWarnings", Now.AddSeconds(-30)), new GameHostHealthComponentResponse("backup", "Firing", Now.AddSeconds(-30)),
         new GameHostHealthComponentResponse("storage", "NoWarnings", Now.AddSeconds(-30)), new GameHostHealthComponentResponse("memory", "NoWarnings", Now.AddSeconds(-30)),
         new GameHostHealthComponentResponse("cpu", "Observed", Now.AddSeconds(-30))], [], new GameHostWarningsResponse("Available", Now.AddSeconds(-10), []));
}
