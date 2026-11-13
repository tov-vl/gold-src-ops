using System.Globalization;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class CommandDispatchHealthReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CommandDispatch_preserves_independent_cycle_and_warning_times_when_other_sources_fail()
    {
        var report = Report(Snapshot());
        report.Should().Contain("Snapshot: Unknown").And.Contain("Polling observation: Unknown")
            .And.Contain("Command dispatcher snapshot evaluated: 2026-11-13 11:59:55 UTC")
            .And.Contain("Command dispatcher snapshot age (seconds): 5")
            .And.Contain("Command dispatcher worker last observed: 2026-11-13 11:59:50 UTC")
            .And.Contain("Command dispatcher last cycle started: 2026-11-13 11:59:48 UTC")
            .And.Contain("Command dispatcher last cycle completed: 2026-11-13 11:59:49 UTC")
            .And.Contain("Command dispatcher completion age (seconds): 11")
            .And.Contain("Command dispatcher last successful cycle: 2026-11-13 11:59:49 UTC")
            .And.Contain("Command dispatcher last failed cycle: 2026-11-13 11:58:20 UTC")
            .And.Contain("Command dispatcher last cycle duration (seconds): 1.125")
            .And.Contain("Command dispatcher consecutive failures: 0").And.Contain("Command dispatcher cycle in progress: No")
            .And.Contain("Command dispatcher observation budget (seconds): 660")
            .And.Contain("Command dispatcher warning snapshot: Available; observed: 2026-11-13 11:59:45 UTC")
            .And.Contain("CommandDispatchMonitorUnavailable: Inactive").And.Contain("CommandDispatchCycleStalled: Pending")
            .And.Contain("CommandDispatchCycleFailed: Firing").And.Contain(CommandDispatchHealthView.Runbook)
            .And.Contain("Running does not establish command delivery").And.Contain("not a command timeout or latency SLO");
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Running")]
    [InlineData("Error")]
    [InlineData("Disabled")]
    [InlineData("Stale")]
    public void Fixed_states_do_not_reinterpret_independent_warnings(string state) =>
        Report(Snapshot() with { ObservationState = state }).Should().Contain("Command dispatcher observation: " + state)
            .And.Contain("CommandDispatchCycleFailed: Firing");

    [Theory]
    [InlineData(-120, "Running")]
    [InlineData(-121, "Stale")]
    [InlineData(1, "Unknown")]
    public void Worker_freshness_is_revalidated_at_generation(int offset, string state)
    {
        var report = Report(Snapshot() with { EvaluatedAtUtc = Now, ObservedAtUtc = Now.AddSeconds(offset), CompletionAgeSeconds = 11 });
        report.Should().Contain("Command dispatcher observation: " + state).And.Contain("Command dispatcher warning snapshot: Available");
        if (state is not "Running")
        {
            report.Should().Contain("Command dispatcher last cycle completed: Unknown").And.Contain("Command dispatcher consecutive failures: Unknown");
        }
    }

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    [InlineData(1, "Unknown")]
    public void Warning_freshness_does_not_change_worker_progress(int offset, string state)
    {
        var report = Report(Snapshot() with { WarningsObservedAtUtc = Now.AddSeconds(offset) });
        report.Should().Contain("Command dispatcher observation: Running").And.Contain("Command dispatcher warning snapshot: " + state);
        if (state is not "Available")
        {
            report.Should().Contain("CommandDispatchCycleFailed: Unknown").And.NotContain("CommandDispatchCycleFailed: Firing");
        }
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(1)]
    public void Invalid_api_snapshot_cannot_become_a_new_observation(int offset)
    {
        var report = GameHostHealthReport.Create(OperatorHealthOverviewFixtures.GameHost(Now), Now, commandDispatch: Snapshot() with { EvaluatedAtUtc = Now.AddSeconds(offset) });
        report.Should().Contain("Snapshot: Available").And.Contain("Command dispatcher observation: Unknown")
            .And.Contain("Command dispatcher last cycle completed: Unknown").And.Contain("Command dispatcher warning snapshot: Unknown");
        if (offset > 0)
        {
            report.Should().Contain("Command dispatcher snapshot evaluated: Unknown").And.Contain("Command dispatcher snapshot age (seconds): Unknown");
        }
    }

    [Fact]
    public void Completion_age_crossing_the_budget_does_not_keep_running()
    {
        Report(Snapshot() with { LastCycleStartedAtUtc = Now.AddSeconds(-662), LastCycleFinishedAtUtc = Now.AddSeconds(-661), CompletionAgeSeconds = 656 })
            .Should().Contain("Command dispatcher observation: Stale").And.Contain("Command dispatcher completion age (seconds): 661");
    }

    [Fact]
    public void Missing_invalid_and_oversized_input_cannot_reflect_provider_material()
    {
        const string sentinel = "private-sentinel\r\nAuthorization: secret\e[31m";
        Report(source: null).Should().Contain("Command dispatcher observation: Unknown").And.Contain("Command dispatcher cycle in progress: Unknown");
        var report = Report(Snapshot() with
        {
            ObservationState = sentinel,
            LastCycleDurationSeconds = double.NaN,
            Warnings = [.. Enumerable.Repeat(new CommandDispatchWarningResponse(sentinel, sentinel), 100)]
        });
        report.Should().Contain("Command dispatcher observation: Unknown").And.Contain("Command dispatcher warning snapshot: Unknown")
            .And.NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("secret").And.NotContain("\e");
        Encoding.UTF8.GetByteCount(report).Should().BeLessThan(32768);
    }

    [Fact]
    public void Null_rules_and_unknown_success_remain_explicit()
    {
        Report(Snapshot() with { Warnings = [null!, null!, null!], LastSuccessfulCycleAtUtc = null, LastFailureAtUtc = null, InProgress = true })
            .Should().Contain("Command dispatcher observation: Running").And.Contain("Command dispatcher warning snapshot: Unknown")
            .And.Contain("Command dispatcher last successful cycle: Unknown").And.Contain("Command dispatcher last failed cycle: Unknown")
            .And.Contain("Command dispatcher cycle in progress: Yes");
    }

    [Fact]
    public void Output_uses_invariant_numbers_and_preserves_zero()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            Report(Snapshot() with { LastCycleDurationSeconds = 0, ConsecutiveFailures = 0 })
                .Should().Contain("Command dispatcher last cycle duration (seconds): 0").And.Contain("Command dispatcher consecutive failures: 0");
            Report(Snapshot()).Should().Contain("Command dispatcher last cycle duration (seconds): 1.125");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static CommandDispatchHealthResponse Snapshot() => CommandDispatchHealthFixture.Valid(Now) with
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
        Warnings = [.. CommandDispatchHealthView.Definitions.Select(static definition => new CommandDispatchWarningResponse(definition.Name,
            definition.Name switch { "CommandDispatchCycleFailed" => "Firing", "CommandDispatchCycleStalled" => "Pending", _ => "Inactive" }))]
    };

    private static string Report(CommandDispatchHealthResponse? source) => GameHostHealthReport.Create(snapshot: null, Now, commandDispatch: source);
}
