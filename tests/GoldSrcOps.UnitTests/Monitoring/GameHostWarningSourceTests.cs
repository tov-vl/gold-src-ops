using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Infrastructure.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class GameHostWarningSourceTests
{
    [Fact]
    public async Task Reads_only_fixed_groups_and_discards_provider_annotations_labels_and_paths()
    {
        using var handler = new FixtureHandler(Body());
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var result = await new GameHostWarningSource(client).ReadAsync(CancellationToken.None);
        result.Should().HaveCount(17);
        JsonSerializer.Serialize(result).Should().NotContain("private-sentinel");
        handler.Uri!.AbsolutePath.Should().Be("/api/v1/rules");
        handler.Uri.Query.Should().Contain("type=alert").And.Contain("exclude_alerts=true");
        Uri.UnescapeDataString(handler.Uri.Query).Split("rule_group[]=").Should().HaveCount(5);
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"status\":\"error\"}")]
    [InlineData("{\"status\":\"success\",\"warnings\":[\"partial\"]}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"groups\":[]}}")]
    public async Task Malformed_partial_and_missing_groups_are_unavailable(string body)
    {
        using var handler = new FixtureHandler(body);
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        (await new GameHostWarningSource(client).ReadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("pagination")]
    [InlineData("null-name")]
    [InlineData("unknown-group")]
    [InlineData("duplicate-group")]
    [InlineData("extra-rule")]
    public async Task Bounded_protocol_never_accepts_truncation_or_ambiguous_scope(string kind)
    {
        var body = kind switch
        {
            "oversized" => new string('x', 65537),
            "pagination" => Body().Replace("\"groups\":", "\"groupNextToken\":\"next\",\"groups\":", StringComparison.Ordinal),
            "null-name" => Body().Replace("\"name\":\"GameEventAgentMonitorUnavailable\"", "\"name\":null", StringComparison.Ordinal),
            "unknown-group" => Body().Replace("goldsrcops-player-backup", "private-sentinel", StringComparison.Ordinal),
            "duplicate-group" => Body().Replace("goldsrcops-player-backup", "goldsrcops-game-event-agent", StringComparison.Ordinal),
            _ => Body(extraRule: true)
        };
        using var handler = new FixtureHandler(body);
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        (await new GameHostWarningSource(client).ReadAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Disabled_source_and_http_failure_do_not_retry()
    {
        using var handler = new FixtureHandler(Body(), HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler);
        (await new GameHostWarningSource(client).ReadAsync(CancellationToken.None)).Should().BeNull();
        handler.Calls.Should().Be(0);
        client.BaseAddress = new Uri("http://prometheus.test/");
        (await new GameHostWarningSource(client).ReadAsync(CancellationToken.None)).Should().BeNull();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_retry()
    {
        using var handler = new FixtureHandler(Body(), delay: true);
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var source = new GameHostWarningSource(client);
        var token = cancellation.Token;
        var action = () => source.ReadAsync(token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Prometheus_nanosecond_timestamps_are_parsed_at_dotnet_precision()
    {
        var body = Body().Replace("2026-11-07T12:00:00+00:00", "2026-11-07T12:00:00.123456789Z", StringComparison.Ordinal);
        using var handler = new FixtureHandler(body);
        using var client = new HttpClient(handler);
        client.BaseAddress = new Uri("http://prometheus.test/");
        var result = await new GameHostWarningSource(client).ReadAsync(CancellationToken.None);
        result.Should().HaveCount(17);
        result.Should().OnlyContain(static value => value.LastEvaluationUtc == GameHostWarningFixture.Now.AddTicks(1234567));
    }

    private static string Body(bool extraRule = false)
    {
        var samples = GameHostWarningFixture.Fresh();
        if (extraRule)
        {
            samples.Add(samples[0]);
        }

        return JsonSerializer.Serialize(new
        {
            status = "success",
            data = new
            {
                groups = samples.GroupBy(static value => value.Group, StringComparer.Ordinal).Select(group => new
                {
                    name = group.Key,
                    file = "/private-sentinel/rules.yml",
                    rules = group.Select(static value => new
                    {
                        name = value.Name,
                        type = "alerting",
                        state = value.State,
                        health = value.Health,
                        lastEvaluation = value.LastEvaluationUtc,
                        query = "private-sentinel",
                        annotations = new { description = "private-sentinel" },
                        labels = new { component = value.Component, severity = value.Severity, instance = "private-sentinel" }
                    })
                })
            }
        });
    }

    private sealed class FixtureHandler(string body, HttpStatusCode status = HttpStatusCode.OK, bool delay = false) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            request.Method.Should().Be(HttpMethod.Get);
            if (delay)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
