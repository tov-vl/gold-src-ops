using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.UnitTests.Helpers;

namespace GoldSrcOps.UnitTests.Api;

public sealed class MetricsEndpointIntegrationTests
{
    [Fact]
    public async Task Unavailable_otlp_endpoint_does_not_affect_readiness_or_direct_metrics()
    {
        await using var factory = new GoldSrcOpsApiFactory(
            configurationOverrides: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Telemetry:Otlp:Enabled"] = "true",
                ["Telemetry:Otlp:Endpoint"] = "http://127.0.0.1:1",
                ["Telemetry:Otlp:Protocol"] = "http/protobuf",
                ["Telemetry:Otlp:ExportIntervalMilliseconds"] = "1000",
                ["Telemetry:Otlp:ExportTimeoutMilliseconds"] = "100"
            });
        using var client = factory.CreateClient();
        GoldSrcOpsMetrics.RecordPollingRun(new ServerPollingResult(
            DueServers: 1,
            SuccessfulPolls: 1,
            FailedPolls: 0,
            OpenedIncidents: 0,
            ClosedIncidents: 0));
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));

        var readinessResponse = await client.GetAsync("/health/ready");
        var metricsResponse = await client.GetAsync("/metrics");

        readinessResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        metricsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var metricsBody = await metricsResponse.Content.ReadAsStringAsync();
        metricsBody.Should().Contain("goldsrcops_polling_runs");
    }

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
        GoldSrcOpsMetrics.RecordAlertReplayRequest(AlertReplayMetricResult.Accepted);
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
        body.Should().Contain("goldsrcops_alerts_replay_requests");
        body.Should().Contain("result=\"accepted\"");
        body.Should().Contain("goldsrcops_commands_queued");
        body.Should().Contain("goldsrcops_commands_dispatched");
        body.Should().Contain("goldsrcops_commands_completed");
        body.Should().Contain("command_type=\"Say\"");
        body.Should().Contain("result=\"auth_failed\"");
        body.Should().Contain("goldsrcops_snapshot_retention_runs");
        body.Should().Contain("goldsrcops_snapshot_retention_snapshots_deleted");
        body.Should().Contain("goldsrcops_snapshot_retention_duration");
    }

    [Fact]
    public async Task Invalid_replay_request_records_invalid_outcome()
    {
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.Operator("operator-42"));
        using var client = factory.CreateClient();
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/alert-delivery/dead-letters/{Guid.NewGuid():D}/replay")
        {
            Content = JsonContent.Create(new ReplayDeadLetterRequest("receiver restored"))
        };
        request.Headers.Add("Idempotency-Key", "not-a-uuid");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.alerts.replay_requests" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "invalid"));
    }

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);
}
