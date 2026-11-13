using System.Globalization;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class RetentionHealthReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retention_preserves_independent_cycle_and_warning_times_when_other_sources_fail()
    {
        var report = Report(Snapshot());
        report.Should().Contain("Snapshot: Unknown").And.Contain("Polling observation: Unknown")
            .And.Contain("Retention snapshot evaluated: 2026-11-13 11:59:55 UTC")
            .And.Contain("Retention snapshot age (seconds): 5")
            .And.Contain("Retention worker last observed: 2026-11-13 11:59:50 UTC")
            .And.Contain("Retention last cycle started: 2026-11-13 11:59:48 UTC")
            .And.Contain("Retention last cycle completed: 2026-11-13 11:59:49 UTC")
            .And.Contain("Retention completion age (seconds): 11")
            .And.Contain("Retention last successful cycle: 2026-11-13 11:59:49 UTC")
            .And.Contain("Retention last failed cycle: 2026-11-13 11:58:20 UTC")
            .And.Contain("Retention last cycle duration (seconds): 1.125")
            .And.Contain("Retention consecutive failures: 0").And.Contain("Retention cycle in progress: No")
            .And.Contain("Retention observation budget (seconds): 660")
            .And.Contain("Retention warning snapshot: Available; observed: 2026-11-13 11:59:45 UTC")
            .And.Contain("SnapshotRetentionMonitorUnavailable: Inactive").And.Contain("SnapshotRetentionCycleStalled: Pending")
            .And.Contain("SnapshotRetentionCycleFailed: Firing").And.Contain(RetentionHealthView.Runbook)
            .And.Contain("Success does not establish backlog size").And.Contain("not a query timeout or latency SLO");
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Running")]
    [InlineData("Error")]
    [InlineData("Disabled")]
    [InlineData("Stale")]
    public void Fixed_states_do_not_reinterpret_independent_warnings(string state) =>
        Report(Snapshot() with { ObservationState = state }).Should().Contain("Retention observation: " + state)
            .And.Contain("SnapshotRetentionCycleFailed: Firing");

    [Theory]
    [InlineData(-120, "Running")]
    [InlineData(-121, "Stale")]
    [InlineData(1, "Unknown")]
    public void Worker_freshness_is_revalidated_at_generation(int offset, string state)
    {
        var report = Report(Snapshot() with { EvaluatedAtUtc = Now, ObservedAtUtc = Now.AddSeconds(offset), CompletionAgeSeconds = 11 });
        report.Should().Contain("Retention observation: " + state).And.Contain("Retention warning snapshot: Available");
        if (state is not "Running")
        {
            report.Should().Contain("Retention last cycle completed: Unknown").And.Contain("Retention consecutive failures: Unknown");
        }
    }

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    [InlineData(1, "Unknown")]
    public void Warning_freshness_does_not_change_worker_progress(int offset, string state)
    {
        var report = Report(Snapshot() with { WarningsObservedAtUtc = Now.AddSeconds(offset) });
        report.Should().Contain("Retention observation: Running").And.Contain("Retention warning snapshot: " + state);
        if (state is not "Available")
        {
            report.Should().Contain("SnapshotRetentionCycleFailed: Unknown").And.NotContain("SnapshotRetentionCycleFailed: Firing");
        }
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(1)]
    public void Invalid_api_snapshot_cannot_become_a_new_observation(int offset)
    {
        var report = GameHostHealthReport.Create(OperatorHealthOverviewFixtures.GameHost(Now), Now, retention: Snapshot() with { EvaluatedAtUtc = Now.AddSeconds(offset) });
        report.Should().Contain("Snapshot: Available").And.Contain("Retention observation: Unknown")
            .And.Contain("Retention last cycle completed: Unknown").And.Contain("Retention warning snapshot: Unknown");
        if (offset > 0)
        {
            report.Should().Contain("Retention snapshot evaluated: Unknown").And.Contain("Retention snapshot age (seconds): Unknown");
        }
    }

    [Fact]
    public void Completion_age_crossing_the_budget_does_not_keep_running()
    {
        Report(Snapshot() with { LastCycleStartedAtUtc = Now.AddSeconds(-662), LastCycleFinishedAtUtc = Now.AddSeconds(-661), CompletionAgeSeconds = 656 })
            .Should().Contain("Retention observation: Stale").And.Contain("Retention completion age (seconds): 661");
    }

    [Fact]
    public void Missing_invalid_and_oversized_input_cannot_reflect_provider_material()
    {
        const string sentinel = "private-sentinel\r\nAuthorization: secret\e[31m";
        Report(source: null).Should().Contain("Retention observation: Unknown").And.Contain("Retention cycle in progress: Unknown");
        var report = Report(Snapshot() with
        {
            ObservationState = sentinel,
            LastCycleDurationSeconds = double.NaN,
            Warnings = [.. Enumerable.Repeat(new RetentionWarningResponse(sentinel, sentinel), 100)]
        });
        report.Should().Contain("Retention observation: Unknown").And.Contain("Retention warning snapshot: Unknown")
            .And.NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("secret").And.NotContain("\e");
        Encoding.UTF8.GetByteCount(report).Should().BeLessThan(32768);
    }

    [Fact]
    public void Null_rules_and_unknown_success_remain_explicit()
    {
        Report(Snapshot() with { Warnings = [null!, null!, null!], LastSuccessfulCycleAtUtc = null, LastFailureAtUtc = null, InProgress = true })
            .Should().Contain("Retention observation: Running").And.Contain("Retention warning snapshot: Unknown")
            .And.Contain("Retention last successful cycle: Unknown").And.Contain("Retention last failed cycle: Unknown")
            .And.Contain("Retention cycle in progress: Yes");
    }

    [Fact]
    public void Output_uses_invariant_numbers_and_preserves_zero()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Report(Snapshot() with { LastCycleDurationSeconds = 0, ConsecutiveFailures = 0 })
                .Should().Contain("Retention last cycle duration (seconds): 0").And.Contain("Retention consecutive failures: 0");
            Report(Snapshot()).Should().Contain("Retention last cycle duration (seconds): 1.125");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static RetentionHealthResponse Snapshot() => RetentionHealthFixture.Valid(Now) with
    {
        EvaluatedAtUtc = Now.AddSeconds(-5),
        ObservedAtUtc = Now.AddSeconds(-10),
        LastCycleStartedAtUtc = Now.AddSeconds(-12),
        LastCycleFinishedAtUtc = Now.AddSeconds(-11),
        CompletionAgeSeconds = 6,
        LastSuccessfulCycleAtUtc = Now.AddSeconds(-11),
        LastFailureAtUtc = Now.AddSeconds(-100),
        LastCycleDurationSeconds = 1.125,
        WarningsObservedAtUtc = Now.AddSeconds(-15),
        Warnings = [.. RetentionHealthView.Definitions.Select(static definition => new RetentionWarningResponse(definition.Name,
            definition.Name switch { "SnapshotRetentionCycleFailed" => "Firing", "SnapshotRetentionCycleStalled" => "Pending", _ => "Inactive" }))]
    };

    private static string Report(RetentionHealthResponse? source) => GameHostHealthReport.Create(snapshot: null, Now, retention: source);
}
