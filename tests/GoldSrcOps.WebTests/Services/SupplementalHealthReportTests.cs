using System.Globalization;
using System.Text;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class SupplementalHealthReportTests
{
    private static DateTimeOffset Now => OperatorHealthOverviewFixtures.Now;

    [Fact]
    public void Six_sections_preserve_minute_precision_and_independent_observation_times()
    {
        var text = Report();
        text.Should().Contain("Credential snapshot: Available").And.Contain("Recorded expiry: 2000-01-01 12:19 UTC")
            .And.Contain("Precision: minute (conservative start)").And.NotContain("2000-01-01 12:19:00")
            .And.Contain("Last verified: 2026-11-11 12:00:00 UTC")
            .And.Contain("Incident outbox snapshot: Available").And.Contain("Dispatcher: Disabled")
            .And.Contain("Source snapshot time: 2026-11-12 11:59:56 UTC")
            .And.Contain("Source snapshot time: 2026-11-12 11:59:55 UTC")
            .And.Contain("Receiver mode: Catch-up / sandbox").And.Contain("Provider worker: Disabled")
            .And.Contain("Unreviewed dead letters: 1").And.Contain("IRM/email is a separate channel");
    }

    [Theory]
    [InlineData(-180, "Available", true)]
    [InlineData(-181, "Stale", false)]
    [InlineData(1, "Unknown", false)]
    public void Credential_freshness_does_not_make_old_deadlines_current(int seconds, string state, bool retained)
    {
        var credentials = OperatorHealthOverviewFixtures.Credentials(Now.AddSeconds(seconds));
        var text = Report(new SupplementalHealthReport.Snapshots(credentials, Outbox: null, Receiver: null, ReceiverConfigured: true));
        text.Should().Contain("Credential snapshot: " + state);
        text.Contains("Read availability metrics: Recorded deadline beyond 30 days", StringComparison.Ordinal).Should().Be(retained);
        text.Should().Contain("Incident outbox snapshot: Unknown").And.Contain("Incident receiver snapshot: Unknown");
    }

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    [InlineData(1, "Unknown")]
    public void Delivery_freshness_uses_generation_time_and_masks_counts(int seconds, string state)
    {
        var outbox = OperatorHealthOverviewFixtures.Outbox(Now) with { ObservedAtUtc = Now.AddSeconds(seconds) };
        var receiver = OperatorHealthOverviewFixtures.Receiver(Now) with { ObservedAtUtc = Now.AddSeconds(seconds) };
        var text = Report(new SupplementalHealthReport.Snapshots(Credentials: null, outbox, receiver, ReceiverConfigured: true));
        text.Should().Contain("Incident outbox snapshot: " + state).And.Contain("Incident receiver snapshot: " + state);
        text.Should().Contain("Pending: " + (state is "Available" ? "2" : state));
    }

    [Fact]
    public void Malformed_catalog_and_queue_fields_cannot_reflect_raw_strings_or_invent_zero()
    {
        var credentials = OperatorHealthOverviewFixtures.Credentials(Now) with
        {
            Entries = [new CredentialExpiryEntryResponse("private-sentinel\r\nAuthorization: secret", "private-sentinel",
                ExpiresAtUtc: null, VerifiedAtUtc: null, RemainingDays: null, "private-sentinel")]
        };
        var outbox = OperatorHealthOverviewFixtures.Outbox(Now) with { PendingCount = -1 };
        var receiver = OperatorHealthOverviewFixtures.Receiver(Now) with { ReceiverMode = "private-sentinel" };
        var text = Report(new SupplementalHealthReport.Snapshots(credentials, outbox, receiver, ReceiverConfigured: true));
        text.Should().Contain("Read availability metrics: Unknown").And.Contain("Pending: Unknown")
            .And.NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("Pending: 0");
    }

    [Fact]
    public void Maximum_counts_are_not_summed_and_output_is_culture_independent_bounded_utf8()
    {
        var outbox = OperatorHealthOverviewFixtures.Outbox(Now) with { PendingCount = long.MaxValue, ProcessingCount = long.MaxValue, DeadLetterCount = long.MaxValue };
        var receiver = OperatorHealthOverviewFixtures.Receiver(Now) with
        {
            PendingCount = long.MaxValue,
            ProcessingCount = long.MaxValue,
            DeadLetterCount = long.MaxValue,
            UnreviewedDeadLetterCount = long.MaxValue,
            ReceiverMode = "Live",
            IsEnabled = true
        };
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            string[] warningNames = ["GameEventAgentMonitorUnavailable", "GameEventAgentObservationInvalid", "GameEventAgentWorkerProblem",
                "GameEventAgentQueueDelayed", "GameEventAgentSpoolProblem", "PlayerBackupMonitorUnavailable", "PlayerBackupObservationInvalid",
                "PlayerBackupInterventionRequired", "PlayerBackupScheduleProblem", "PlayerBackupCopyUnavailable", "GameHostStorageMonitorUnavailable",
                "GameHostStorageObservationInvalid", "GameHostStorageBytesLow", "GameHostStorageInodesLow", "GameHostResourceMonitorUnavailable",
                "GameHostResourceObservationInvalid", "GameHostMemoryLow"];
            var game = OperatorHealthOverviewFixtures.GameHost(Now) with
            {
                Warnings = new GameHostWarningsResponse("Available", Now, [.. warningNames.Select(static name => new GameHostWarningResponse(name, "", "Firing", ""))])
            };
            var archive = OperatorHealthOverviewFixtures.Archive(Now) with
            {
                Warnings = [.. ArchiveHealthView.Definitions.Select(static definition => new ArchiveWarningResponse(definition.Name, "Firing"))]
            };
            var polling = OperatorHealthOverviewFixtures.Polling(Now) with
            {
                Warnings = [.. PollingHealthView.Definitions.Select(static definition => new PollingWarningResponse(definition.Name, "Firing"))]
            };
            var text = GameHostHealthReport.Create(game, Now, archive, polling,
                new SupplementalHealthReport.Snapshots(OperatorHealthOverviewFixtures.Credentials(Now), outbox, receiver, ReceiverConfigured: true), RetentionHealthFixture.Valid(Now) with
                { Warnings = [.. RetentionHealthView.Definitions.Select(static definition => new RetentionWarningResponse(definition.Name, "Firing"))] },
                CommandDispatchHealthFixture.Valid(Now) with { Warnings = [.. CommandDispatchHealthView.Definitions.Select(static definition => new CommandDispatchWarningResponse(definition.Name, "Firing"))] });
            text.Should().Contain("9223372036854775807").And.Contain("Receiver mode: Live").And.Contain("Provider worker: Enabled");
            Encoding.UTF8.GetByteCount(text).Should().BeLessThan(32768);
            text.Should().Contain("SnapshotRetentionCycleFailed: Firing").And.Contain("CommandDispatchCycleFailed: Firing");
            foreach (var name in warningNames)
            {
                text.Should().Contain(name + ": Firing");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static string Report(SupplementalHealthReport.Snapshots? supplement = null) => GameHostHealthReport.Create(
        OperatorHealthOverviewFixtures.GameHost(Now), Now, OperatorHealthOverviewFixtures.Archive(Now), OperatorHealthOverviewFixtures.Polling(Now),
        supplement ?? new SupplementalHealthReport.Snapshots(OperatorHealthOverviewFixtures.Credentials(Now), OperatorHealthOverviewFixtures.Outbox(Now),
            OperatorHealthOverviewFixtures.Receiver(Now), ReceiverConfigured: true));
}
