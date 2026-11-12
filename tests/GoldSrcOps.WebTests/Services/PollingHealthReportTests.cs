using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class PollingHealthReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Polling_keeps_independent_timestamps_progress_and_all_three_rule_states()
    {
        var report = Report(Snapshot());
        report.Should().Contain("Snapshot: Unknown").And.Contain("Archive observation: Unknown")
            .And.Contain("Polling snapshot evaluated: 2026-11-12 11:59:55 UTC")
            .And.Contain("Polling snapshot age (seconds): 5")
            .And.Contain("Worker last observed: 2026-11-12 11:59:50 UTC")
            .And.Contain("Last cycle started: 2026-11-12 11:59:48 UTC")
            .And.Contain("Last cycle completed: 2026-11-12 11:59:49 UTC")
            .And.Contain("Completion age (seconds): 11").And.Contain("Observation budget (seconds): 370")
            .And.Contain("PollingMonitorUnavailable: Inactive").And.Contain("PollingCycleStalled: Pending")
            .And.Contain("PollingCycleFailed: Firing").And.Contain(PollingHealthView.Runbook)
            .And.Contain("not a query deadline or latency SLO");
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Running")]
    [InlineData("Error")]
    [InlineData("Disabled")]
    [InlineData("Stale")]
    public void Fixed_worker_states_do_not_reinterpret_rule_states(string state)
    {
        Report(Snapshot() with { ObservationState = state }).Should().Contain("Polling observation: " + state)
            .And.Contain("PollingCycleFailed: Firing");
    }

    [Theory]
    [InlineData(-120, "Running")]
    [InlineData(-121, "Stale")]
    [InlineData(1, "Unknown")]
    public void Observer_freshness_is_revalidated_at_report_generation(int offset, string state)
    {
        var source = Snapshot() with { EvaluatedAtUtc = Now, ObservedAtUtc = Now.AddSeconds(offset), CompletionAgeSeconds = 11 };
        var report = Report(source);
        report.Should().Contain("Polling observation: " + state).And.Contain("Polling warning snapshot: Available");
        if (state is not "Running")
        {
            report.Should().Contain("Last cycle completed: Unknown").And.Contain("Observation budget (seconds): Unknown");
        }
    }

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    [InlineData(1, "Unknown")]
    public void Rule_freshness_is_independent_of_worker_progress(int offset, string state)
    {
        var report = Report(Snapshot() with { WarningsObservedAtUtc = Now.AddSeconds(offset) });
        report.Should().Contain("Polling observation: Running").And.Contain("Polling warning snapshot: " + state);
        if (state is not "Available")
        {
            report.Should().Contain("PollingCycleFailed: Unknown").And.NotContain("PollingCycleFailed: Firing");
        }
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(1)]
    public void Invalid_whole_snapshot_masks_polling_and_does_not_hide_other_sources(int offset)
    {
        var archive = new ArchiveHealthResponse(Now, "Available", Now, HasScheduledSuccess: false, LastSuccessAtUtc: null, LastSuccessAgeSeconds: null,
            "Unknown", WarningsObservedAtUtc: null, []);
        var game = new GameHostHealthResponse(Now, [], [], new GameHostWarningsResponse("Available", Now, []));
        var report = GameHostHealthReport.Create(game, Now, archive, Snapshot() with { EvaluatedAtUtc = Now.AddSeconds(offset) });
        report.Should().Contain("Snapshot: Available").And.Contain("Archive observation: Available")
            .And.Contain("Polling observation: Unknown").And.Contain("Polling warning snapshot: Unknown");
    }

    [Fact]
    public void Completion_age_crossing_the_budget_cannot_keep_a_retained_running_state()
    {
        var source = Snapshot() with { LastCycleStartedAtUtc = Now.AddSeconds(-372), LastCycleFinishedAtUtc = Now.AddSeconds(-371), CompletionAgeSeconds = 366 };
        Report(source).Should().Contain("Polling observation: Stale").And.Contain("Completion age (seconds): 371");
    }

    [Fact]
    public void Missing_or_malformed_polling_never_reflects_provider_material()
    {
        const string sentinel = "private-sentinel\r\nAuthorization: secret\e[31m";
        Report(source: null).Should().Contain("Polling observation: Unknown").And.Contain("PollingCycleFailed: Unknown");
        var report = Report(Snapshot() with
        {
            ObservationState = sentinel,
            CompletionAgeSeconds = double.NaN,
            Warnings = [.. Enumerable.Repeat(new PollingWarningResponse(sentinel, sentinel), 100)]
        });
        report.Should().Contain("Polling observation: Unknown").And.Contain("Polling warning snapshot: Unknown")
            .And.NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("secret").And.NotContain("\e");
        Encoding.UTF8.GetByteCount(report).Should().BeLessThan(32768);
    }

    [Fact]
    public void Null_rule_items_from_json_stay_unknown_without_hiding_the_worker()
    {
        Report(Snapshot() with { Warnings = [null!, null!, null!] }).Should().Contain("Polling observation: Running")
            .And.Contain("Polling warning snapshot: Unknown");
    }

    internal static PollingHealthResponse Snapshot() => new(Now.AddSeconds(-5), "Running", Now.AddSeconds(-10), Now.AddSeconds(-12), Now.AddSeconds(-11), 6, 370,
        "Available", Now.AddSeconds(-15), [.. PollingHealthView.Definitions.Select(static definition => new PollingWarningResponse(definition.Name,
            definition.Name switch { "PollingCycleFailed" => "Firing", "PollingCycleStalled" => "Pending", _ => "Inactive" }))]);

    private static string Report(PollingHealthResponse? source) => GameHostHealthReport.Create(snapshot: null, Now, archive: null, source);
}
