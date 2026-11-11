using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class ArchiveHealthReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Archive_keeps_its_own_timestamps_and_all_four_rule_states_when_game_host_is_unavailable()
    {
        var source = Snapshot();
        var report = GameHostHealthReport.Create(snapshot: null, Now, source);
        report.Should().Contain("Snapshot: Unknown").And.Contain("Archive observation: Available")
            .And.Contain("Archive snapshot evaluated: 2026-11-11 11:59:55 UTC")
            .And.Contain("Archive observer last checked: 2026-11-11 11:50:00 UTC")
            .And.Contain("Last successful scheduled run age (seconds): 14400")
            .And.Contain("ArchiveScheduleDelayed: Firing").And.Contain("ArchiveScheduleCatchupAtRisk: Pending")
            .And.Contain("ArchiveScheduleMonitorUnavailable: Inactive").And.Contain("ArchiveScheduleObservationInvalid: Inactive")
            .And.Contain(ArchiveHealthView.Runbook).And.Contain("do not automatically dispatch or retry");
    }

    [Theory]
    [InlineData(-1800, "Available")]
    [InlineData(-1801, "Stale")]
    [InlineData(1, "Unknown")]
    public void Observer_boundary_is_independent_of_game_host_and_rules(int seconds, string state)
    {
        var source = Snapshot() with { EvaluatedAtUtc = Now, ObservedAtUtc = Now.AddSeconds(seconds), LastSuccessAgeSeconds = 14400 };
        var report = GameHostHealthReport.Create(snapshot: null, Now, source);
        report.Should().Contain("Archive observation: " + state).And.Contain("Archive warning snapshot: Available");
        if (state is not "Available")
        {
            report.Should().Contain("Last successful scheduled run: Unknown").And.Contain("Accepted scheduled success found: Unknown");
        }
    }

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    [InlineData(1, "Unknown")]
    public void Rule_freshness_is_separate_from_observation(int seconds, string state)
    {
        var source = Snapshot() with { EvaluatedAtUtc = Now, WarningsObservedAtUtc = Now.AddSeconds(seconds), LastSuccessAgeSeconds = 14400 };
        var report = GameHostHealthReport.Create(snapshot: null, Now, source);
        report.Should().Contain("Archive observation: Available").And.Contain("Archive warning snapshot: " + state);
        if (state is not "Available")
        {
            report.Should().Contain("ArchiveScheduleDelayed: Unknown").And.NotContain("ArchiveScheduleDelayed: Firing");
        }
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(1)]
    public void Old_or_future_api_snapshot_masks_both_archive_sections(int seconds)
    {
        var report = GameHostHealthReport.Create(snapshot: null, Now, Snapshot() with { EvaluatedAtUtc = Now.AddSeconds(seconds) });
        report.Should().Contain("Archive observation: Unknown").And.Contain("Archive warning snapshot: Unknown")
            .And.Contain("Last successful scheduled run: Unknown");
    }

    [Fact]
    public void No_accepted_success_is_explicit_and_zero_age_is_valid()
    {
        var source = Snapshot() with { HasScheduledSuccess = false, LastSuccessAtUtc = null, LastSuccessAgeSeconds = null };
        GameHostHealthReport.Create(snapshot: null, Now, source).Should().Contain("Accepted scheduled success found: No")
            .And.Contain("Last successful scheduled run age (seconds): Unknown");
        source = Snapshot() with { EvaluatedAtUtc = Now, ObservedAtUtc = Now, LastSuccessAtUtc = Now, LastSuccessAgeSeconds = 0 };
        GameHostHealthReport.Create(snapshot: null, Now, source).Should().Contain("Accepted scheduled success found: Yes")
            .And.Contain("Last successful scheduled run age (seconds): 0");
    }

    [Fact]
    public void Invalid_numbers_and_untrusted_rule_collections_are_never_reflected()
    {
        const string sentinel = "private-sentinel\r\nAuthorization: secret\e[31m";
        var source = Snapshot() with
        {
            LastSuccessAgeSeconds = double.NaN,
            Warnings = [.. Enumerable.Repeat(new ArchiveWarningResponse(sentinel, sentinel), 100)]
        };
        var report = GameHostHealthReport.Create(snapshot: null, Now, source);
        report.Should().Contain("Archive observation: Unknown").And.Contain("Archive warning snapshot: Unknown")
            .And.NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("secret").And.NotContain("\e");
        Encoding.UTF8.GetByteCount(report).Should().BeLessThan(32768);
    }

    [Fact]
    public void Maximal_known_game_host_and_archive_report_is_bounded()
    {
        var now = Now;
        string[] names = ["GameEventAgentMonitorUnavailable", "GameEventAgentObservationInvalid", "GameEventAgentWorkerProblem", "GameEventAgentQueueDelayed", "GameEventAgentSpoolProblem",
            "PlayerBackupMonitorUnavailable", "PlayerBackupObservationInvalid", "PlayerBackupInterventionRequired", "PlayerBackupScheduleProblem", "PlayerBackupCopyUnavailable",
            "GameHostStorageMonitorUnavailable", "GameHostStorageObservationInvalid", "GameHostStorageBytesLow", "GameHostStorageInodesLow", "GameHostResourceMonitorUnavailable",
            "GameHostResourceObservationInvalid", "GameHostMemoryLow"];
        var game = new GameHostHealthResponse(now, [], [], new GameHostWarningsResponse("Available", now,
            [.. names.Select(name => new GameHostWarningResponse(name, "", "Firing", ""))]));
        var report = GameHostHealthReport.Create(game, now, Snapshot());
        report.Should().Contain("Warning snapshot: Available");
        foreach (var name in names)
        {
            report.Should().Contain(name + ": Firing");
        }
        Encoding.UTF8.GetByteCount(report).Should().BeLessThan(32768);
    }

    private static ArchiveHealthResponse Snapshot() => new(Now.AddSeconds(-5), "Available", Now.AddMinutes(-10), HasScheduledSuccess: true, Now.AddHours(-4), 14395,
        "Available", Now.AddSeconds(-10), [.. ArchiveHealthView.Definitions.Select(static definition => new ArchiveWarningResponse(definition.Name,
            definition.Name switch { "ArchiveScheduleDelayed" => "Firing", "ArchiveScheduleCatchupAtRisk" => "Pending", _ => "Inactive" }))]);
}
