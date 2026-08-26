using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.UnitTests.Api;

public sealed class MetricsEndpointIntegrationTests
{
    [Fact]
    public async Task GetMetrics_returns_prometheus_metrics_with_application_polling_series()
    {
        await using var factory = new GoldSrcOpsApiFactory();
        using var client = factory.CreateClient();
        GoldSrcOpsMetrics.RecordPollingRun(new ServerPollingResult(
            DueServers: 2,
            SuccessfulPolls: 1,
            FailedPolls: 1,
            OpenedIncidents: 1,
            ClosedIncidents: 0));
        GoldSrcOpsMetrics.RecordAlertEnqueued(IncidentAlertEvents.ServerUnavailable);
        GoldSrcOpsMetrics.RecordAlertDeliveryAttempt(
            AlertDeliveryMetricResult.RetryScheduled,
            TimeSpan.FromMilliseconds(40));
        GoldSrcOpsMetrics.RecordAlertClaimsRecovered(2);
        GoldSrcOpsMetrics.RecordAlertDeadLetters(1);
        GoldSrcOpsMetrics.RecordAlertProcessedMessagesDeleted(3);
        GoldSrcOpsMetrics.UpdateAlertOutboxStatistics(
            pendingCount: 4,
            oldestPendingAge: TimeSpan.FromMinutes(2),
            deadLetterCount: 1);
        GoldSrcOpsMetrics.RecordCommandQueued(ServerCommandType.Say);
        GoldSrcOpsMetrics.RecordCommandDispatched(ServerCommandType.Say);
        GoldSrcOpsMetrics.RecordCommandCompleted(ServerCommandType.Say, CommandDispatchMetricResult.AuthenticationFailed);
        GoldSrcOpsMetrics.RecordSnapshotRetentionCompleted(2, TimeSpan.FromMilliseconds(25));

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("goldsrcops_polling_runs");
        body.Should().Contain("goldsrcops_polling_server_poll_attempts");
        body.Should().Contain("result=\"success\"");
        body.Should().Contain("result=\"failure\"");
        body.Should().Contain("goldsrcops_alerts_enqueued");
        body.Should().Contain($"event_type=\"{IncidentAlertEvents.ServerUnavailable}\"");
        body.Should().Contain("goldsrcops_alerts_delivery_attempts");
        body.Should().Contain("result=\"retry_scheduled\"");
        body.Should().Contain("goldsrcops_alerts_delivery_duration");
        body.Should().Contain("goldsrcops_alerts_claims_recovered");
        body.Should().Contain("goldsrcops_alerts_dead_letters");
        body.Should().Contain("goldsrcops_alerts_processed_deleted");
        body.Should().Contain("goldsrcops_alerts_pending");
        body.Should().Contain("goldsrcops_alerts_oldest_pending_age");
        body.Should().Contain("goldsrcops_alerts_dead_letter_count");
        body.Should().Contain("goldsrcops_commands_queued");
        body.Should().Contain("goldsrcops_commands_dispatched");
        body.Should().Contain("goldsrcops_commands_completed");
        body.Should().Contain("command_type=\"Say\"");
        body.Should().Contain("result=\"auth_failed\"");
        body.Should().Contain("goldsrcops_snapshot_retention_runs");
        body.Should().Contain("goldsrcops_snapshot_retention_snapshots_deleted");
        body.Should().Contain("goldsrcops_snapshot_retention_duration");
    }
}
