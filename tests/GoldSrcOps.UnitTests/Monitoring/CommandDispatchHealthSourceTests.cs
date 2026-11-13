using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class CommandDispatchHealthSourceTests
{
    [Fact]
    public async Task Exactly_two_bounded_fixed_reads_drop_private_labels_and_provider_text()
    {
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new CommandDispatchHealthSource(client).ReadAsync(CommandDispatchHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().HaveCount(12);
        snapshot.Rules.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
        handler.Query.Should().Contain("goldsrcops_command_dispatch_worker_").And.Contain("gso_command_dispatch_field").And.NotContain("max(").And.NotContain("last_over_time");
        handler.Requests.Should().Contain("GET /api/v1/rules?type=alert&exclude_alerts=true&rule_group%5B%5D=goldsrcops-command-dispatch-liveness");
        JsonSerializer.Serialize(snapshot).Should().NotContain("private-sentinel").And.NotContain("instance").And.NotContain("annotations");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"status\":\"error\"}")]
    [InlineData("{\"status\":\"success\",\"warnings\":[]}")]
    [InlineData("{\"status\":\"success\",\"infos\":[]}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[]}}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{\"gso_command_dispatch_field\":\"has_success\"},\"value\":[1794398400,null]}]}}")]
    public async Task Malformed_metric_side_does_not_hide_usable_rules(string body)
    {
        using var handler = new FixtureHandler { Metrics = body };
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new CommandDispatchHealthSource(client).ReadAsync(CommandDispatchHealthFixture.Now, CancellationToken.None);
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
        var snapshot = await new CommandDispatchHealthSource(client).ReadAsync(CommandDispatchHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().HaveCount(12);
        snapshot.Rules.Should().BeNull();
    }

    [Fact]
    public async Task Byte_limit_failure_is_not_retried_and_disabled_source_does_not_read()
    {
        var oversized = new string('x', 32769);
        using var handler = new FixtureHandler { Metrics = oversized };
        using var client = new HttpClient(handler);
        var source = new CommandDispatchHealthSource(client);
        (await source.ReadAsync(CommandDispatchHealthFixture.Now, CancellationToken.None)).Metrics.Should().BeNull();
        handler.Requests.Should().BeEmpty();
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await source.ReadAsync(CommandDispatchHealthFixture.Now, CancellationToken.None);
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
        var source = new CommandDispatchHealthSource(client);
        var token = cancellation.Token;
        await source.Awaiting(value => value.ReadAsync(CommandDispatchHealthFixture.Now, token)).Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Count.Should().BeLessThanOrEqualTo(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_read_obeys_deadline_and_caller_cancellation(bool callerCancels)
    {
        using var body = new WaitingContent();
        using var handler = new FixtureHandler { MetricsContent = body };
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        using var cancellation = new CancellationTokenSource();
        var pending = new CommandDispatchHealthSource(client).ReadAsync(CommandDispatchHealthFixture.Now, cancellation.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancels)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            result.Metrics.Should().BeNull();
            result.Rules.Should().HaveCount(3);
        }

        handler.Requests.Should().HaveCount(2);
    }

    private sealed class WaitingContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Failed_transport_does_not_retry_or_follow_a_location(HttpStatusCode status)
    {
        using var handler = new FixtureHandler { MetricsStatus = status };
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var snapshot = await new CommandDispatchHealthSource(client).ReadAsync(CommandDispatchHealthFixture.Now, CancellationToken.None);
        snapshot.Metrics.Should().BeNull();
        snapshot.Rules.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();
        public string? Query { get; private set; }
        public HttpStatusCode MetricsStatus { get; init; } = HttpStatusCode.OK;
        public HttpContent? MetricsContent { get; init; }
        public string Metrics { get; init; } = JsonSerializer.Serialize(new { status = "success", data = new { resultType = "vector", result = CommandDispatchHealthFixture.Metrics().Select(sample => new { metric = new { gso_command_dispatch_field = sample.Metric, service_instance_id = sample.Source, instance = "private-sentinel" }, value = new object[] { sample.SampledAtUtc.ToUnixTimeSeconds(), sample.Value.ToString(CultureInfo.InvariantCulture) } }) } });
        public string Rules { get; init; } = JsonSerializer.Serialize(new { status = "success", data = new { groups = new[] { new { name = "goldsrcops-command-dispatch-liveness", file = "private-sentinel", rules = CommandDispatchHealthFixture.Rules().Select(sample => new { name = sample.Name, type = "alerting", health = sample.Health, state = sample.State, lastEvaluation = sample.EvaluatedAtUtc, labels = new { component = sample.Component, severity = sample.Severity }, annotations = new { summary = "private-sentinel" } }) } } } });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request.Method + " " + request.RequestUri!.PathAndQuery);
            request.Headers.Authorization.Should().BeNull();
            if (request.Content is not null)
            {
                Query = Uri.UnescapeDataString(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return new HttpResponseMessage(request.Method == HttpMethod.Post ? MetricsStatus : HttpStatusCode.OK) { Content = request.Method == HttpMethod.Post && MetricsContent is not null ? MetricsContent : new StringContent(request.Method == HttpMethod.Post ? Metrics : Rules, Encoding.UTF8, "application/json") };
        }
    }
}
