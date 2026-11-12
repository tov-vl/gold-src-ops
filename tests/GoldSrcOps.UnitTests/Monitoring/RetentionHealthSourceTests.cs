using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class RetentionHealthSourceTests
{
    [Fact]
    public async Task Exactly_two_bounded_fixed_reads_drop_private_labels_and_provider_text()
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new RetentionHealthSource(client).ReadAsync(RetentionHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().HaveCount(12);
        snapshot.Rules.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
        handler.Query.Should().Contain("goldsrcops_snapshot_retention_worker_").And.Contain("gso_retention_field").And.NotContain("max(").And.NotContain("last_over_time");
        handler.Requests.Should().Contain("GET /api/v1/rules?type=alert&exclude_alerts=true&rule_group%5B%5D=goldsrcops-snapshot-retention-liveness");
        JsonSerializer.Serialize(snapshot).Should().NotContain("private-sentinel").And.NotContain("instance").And.NotContain("annotations");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"error\"}")]
    [InlineData("{\"status\":\"success\",\"warnings\":[]}")]
    [InlineData("{\"status\":\"success\",\"infos\":[]}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[]}}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{\"gso_retention_field\":\"has_success\"},\"value\":[1794398400,null]}]}}")]
    public async Task Malformed_metric_side_does_not_hide_usable_rules(string body)
    {
        using var handler = new FixtureHandler { Metrics = body };
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new RetentionHealthSource(client).ReadAsync(RetentionHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().BeNull();
        snapshot.Rules.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"groupNextToken\":\"private-sentinel\",\"groups\":[]}}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"groups\":[]}}")]
    public async Task Incomplete_rules_do_not_hide_usable_metrics(string body)
    {
        using var handler = new FixtureHandler { Rules = body };
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new RetentionHealthSource(client).ReadAsync(RetentionHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().HaveCount(12);
        snapshot.Rules.Should().BeNull();
    }

    [Fact]
    public async Task Byte_limit_failure_is_not_retried_and_disabled_source_does_not_read()
    {
        using var handler = new FixtureHandler { Metrics = new string('x', 32769) };
        using var client = new HttpClient(handler);
        var source = new RetentionHealthSource(client);
        (await source.ReadAsync(RetentionHealthFixture.Now, CancellationToken.None)).Metrics.Should().BeNull();
        handler.Requests.Should().BeEmpty();
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await source.ReadAsync(RetentionHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().BeNull();
        snapshot.Rules.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_retry()
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var source = new RetentionHealthSource(client);
        var token = cancellation.Token;
        await source.Awaiting(value => value.ReadAsync(RetentionHealthFixture.Now, token)).Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Failed_transport_does_not_retry_or_follow_a_location(HttpStatusCode status)
    {
        using var handler = new FixtureHandler { MetricsStatus = status };
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new RetentionHealthSource(client).ReadAsync(RetentionHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().BeNull();
        snapshot.Rules.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public string? Query { get; private set; }
        public HttpStatusCode MetricsStatus { get; init; } = HttpStatusCode.OK;
        public string Metrics { get; init; } = JsonSerializer.Serialize(new { status = "success", data = new { resultType = "vector", result = RetentionHealthFixture.Metrics().Select(sample => new { metric = new { gso_retention_field = sample.Metric, service_instance_id = sample.Source, instance = "private-sentinel" }, value = new object[] { sample.SampledAtUtc.ToUnixTimeSeconds(), sample.Value.ToString(CultureInfo.InvariantCulture) } }) } });
        public string Rules { get; init; } = JsonSerializer.Serialize(new { status = "success", data = new { groups = new[] { new { name = "goldsrcops-snapshot-retention-liveness", file = "private-sentinel", rules = RetentionHealthFixture.Rules().Select(sample => new { name = sample.Name, type = "alerting", health = sample.Health, state = sample.State, lastEvaluation = sample.EvaluatedAtUtc, labels = new { component = sample.Component, severity = sample.Severity }, annotations = new { summary = "private-sentinel" } }) } } } });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request.Method + " " + request.RequestUri!.PathAndQuery);
            request.Headers.Authorization.Should().BeNull();
            if (request.Content is not null)
            {
                Query = Uri.UnescapeDataString(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return new HttpResponseMessage(request.Method == HttpMethod.Post ? MetricsStatus : HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Post ? Metrics : Rules, Encoding.UTF8, "application/json") };
        }
    }
}
