using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Application.Servers;
using GoldSrcOps.Application.Telemetry;

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

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("goldsrcops_polling_runs");
        body.Should().Contain("goldsrcops_polling_server_poll_attempts");
        body.Should().Contain("result=\"success\"");
        body.Should().Contain("result=\"failure\"");
    }
}
