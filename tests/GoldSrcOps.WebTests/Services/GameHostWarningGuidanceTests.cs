using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class GameHostWarningGuidanceTests
{
    [Theory]
    [InlineData("GameEventAgentMonitorUnavailable", "agent-monitor-unavailable")]
    [InlineData("GameEventAgentObservationInvalid", "agent-observation-invalid")]
    [InlineData("GameEventAgentWorkerProblem", "agent-worker-problem")]
    [InlineData("GameEventAgentQueueDelayed", "agent-queue-delayed")]
    [InlineData("GameEventAgentSpoolProblem", "agent-spool-problem")]
    [InlineData("PlayerBackupMonitorUnavailable", "backup-monitor-unavailable")]
    [InlineData("PlayerBackupObservationInvalid", "backup-observation-invalid")]
    [InlineData("PlayerBackupInterventionRequired", "backup-intervention-required")]
    [InlineData("PlayerBackupScheduleProblem", "backup-schedule-problem")]
    [InlineData("PlayerBackupCopyUnavailable", "backup-copy-unavailable")]
    [InlineData("GameHostStorageMonitorUnavailable", "storage-monitor-unavailable")]
    [InlineData("GameHostStorageObservationInvalid", "storage-observation-invalid")]
    [InlineData("GameHostStorageBytesLow", "storage-bytes-low")]
    [InlineData("GameHostStorageInodesLow", "storage-inodes-low")]
    [InlineData("GameHostResourceMonitorUnavailable", "resource-monitor-unavailable")]
    [InlineData("GameHostResourceObservationInvalid", "resource-observation-invalid")]
    [InlineData("GameHostMemoryLow", "memory-low")]
    public void Known_warning_uses_its_exact_reviewed_guide(string name, string anchor)
    {
        var guidance = GameHostWarningGuidance.ForWarning(name);
        guidance.Should().NotBeNull();
        guidance.FirstCheck.Should().NotBeNullOrWhiteSpace();
        guidance.Preserve.Should().NotBeNullOrWhiteSpace();
        guidance.RunbookUrl.Should().Be("https://github.com/tov-vl/gold-src-ops/blob/main/docs/game-host-warning-triage.md#" + anchor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gamehostmemorylow")]
    [InlineData("GameHostMemoryLow#unexpected")]
    [InlineData("https://unexpected.test/")]
    [InlineData("<script>alert(1)</script>")]
    public void Unknown_names_cannot_supply_guidance_or_a_link(string name) =>
        GameHostWarningGuidance.ForWarning(name).Should().BeNull();

    [Fact]
    public void Unavailable_snapshots_have_distinct_first_checks_and_preservation_notes()
    {
        GameHostWarningGuidance.Unknown.FirstCheck.Should().Contain("Refresh once");
        GameHostWarningGuidance.Unknown.Preserve.Should().Contain("does not mean healthy or offline");
        GameHostWarningGuidance.Unknown.RunbookUrl.Should().EndWith("#unknown-snapshot");
        GameHostWarningGuidance.Stale.FirstCheck.Should().Contain("oldest rule evaluation");
        GameHostWarningGuidance.Stale.Preserve.Should().Contain("both timestamps");
        GameHostWarningGuidance.Stale.RunbookUrl.Should().EndWith("#stale-snapshot");
    }
}
