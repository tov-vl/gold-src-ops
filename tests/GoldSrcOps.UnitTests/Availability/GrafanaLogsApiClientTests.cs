using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Availability;
using GoldSrcOps.AvailabilityExporter;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class GrafanaLogsApiClientTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QueryAsync_returns_only_classified_timestamp_and_kind()
    {
        var observedAt = WindowStart.AddSeconds(11).AddMilliseconds(125);
        const string line =
            "level=ERROR msg=\"Resolution with IP protocol failed\" " +
            "target=https://private.example.test err=\"lookup private.example.test: no such host\"";
        using var handler = new CapturingHttpMessageHandler(
            new StubResponse(HttpStatusCode.OK, CreateLokiResponse((observedAt, line))));
        using var httpClient = CreateHttpClient(handler);
        var client = new GrafanaLogsApiClient(httpClient, CreateOptions());

        var details = await client.QueryAsync(
            WindowStart,
            WindowStart.AddMinutes(1),
            CancellationToken.None);

        details.Should().ContainSingle().Which.Should().Be(
            new ProbeFailureDetail(observedAt, ProbeFailureKind.Dns));
        var request = handler.Requests.Should().ContainSingle().Which;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri.AbsolutePath.Should().Be("/base/loki/api/v1/query_range");
        request.RequestUri.AbsoluteUri.Should().NotContain("sensitive-logs-token");
        request.AuthorizationScheme.Should().Be("Basic");
        request.AuthorizationParameter.Should().NotBeNullOrWhiteSpace();

        var decodedQuery = Uri.UnescapeDataString(request.RequestUri.Query);
        decodedQuery.Should().Contain("check_name=\"http\"");
        decodedQuery.Should().Contain("probe_success=\"0\"");
        decodedQuery.Should().Contain("label_environment=\"production\"");
        decodedQuery.Should().Contain("label_role=\"primary\"");
        decodedQuery.Should().Contain("label_monitor_revision=\"v2-4-shadow-001\"");
        decodedQuery.Should().Contain("limit=101");
        decodedQuery.Should().Contain("direction=forward");
    }

    [Fact]
    public async Task QueryAsync_fails_closed_when_response_exceeds_line_limit()
    {
        const string privateLine =
            "level=ERROR msg=\"HTTP request failed\" target=https://private.example.test " +
            "err=\"private-error-text\"";
        var response = CreateLokiResponse(
            (WindowStart.AddSeconds(1), privateLine),
            (WindowStart.AddSeconds(2), privateLine));
        using var handler = new CapturingHttpMessageHandler(
            new StubResponse(HttpStatusCode.OK, response));
        using var httpClient = CreateHttpClient(handler);
        var client = new GrafanaLogsApiClient(
            httpClient,
            CreateOptions(maximumLines: 1));

        var exception = await Assert.ThrowsAsync<GrafanaLogsApiException>(() =>
            client.QueryAsync(
                WindowStart,
                WindowStart.AddMinutes(1),
                CancellationToken.None));

        exception.Message.Should().Contain("line limit");
        exception.ToString().Should().NotContain("private.example.test");
        exception.ToString().Should().NotContain("private-error-text");
    }

    [Fact]
    public async Task QueryAsync_does_not_include_invalid_response_content_in_an_error()
    {
        const string privateContent = "private-target-and-error-text";
        var response = $$"""
            {
              "status": "success",
              "data": {
                "resultType": "streams",
                "result": [
                  {
                    "stream": {},
                    "values": [["not-a-timestamp", "{{privateContent}}"]]
                  }
                ]
              }
            }
            """;
        using var handler = new CapturingHttpMessageHandler(
            new StubResponse(HttpStatusCode.OK, response));
        using var httpClient = CreateHttpClient(handler);
        var client = new GrafanaLogsApiClient(httpClient, CreateOptions());

        var exception = await Assert.ThrowsAsync<GrafanaLogsApiException>(() =>
            client.QueryAsync(
                WindowStart,
                WindowStart.AddMinutes(1),
                CancellationToken.None));

        exception.Message.Should().Contain("invalid response");
        exception.ToString().Should().NotContain(privateContent);
    }

    [Fact]
    public async Task QueryAsync_rejects_response_content_length_over_the_byte_limit()
    {
        var privateContent = new string('x', 2_048);
        using var handler = new CapturingHttpMessageHandler(
            new StubResponse(HttpStatusCode.OK, privateContent));
        using var httpClient = CreateHttpClient(handler);
        var client = new GrafanaLogsApiClient(
            httpClient,
            CreateOptions(maximumResponseBytes: 1_024));

        var exception = await Assert.ThrowsAsync<GrafanaLogsApiException>(() =>
            client.QueryAsync(
                WindowStart,
                WindowStart.AddMinutes(1),
                CancellationToken.None));

        exception.Message.Should().Contain("size limit");
        exception.ToString().Should().NotContain(privateContent);
    }

    [Fact]
    public void CreateOptional_returns_null_only_when_all_log_settings_are_absent()
    {
        var result = GrafanaLogsApiOptions.CreateOptional(null, null, null, CreateMetricsOptions());

        result.Should().BeNull();
    }

    [Fact]
    public void CreateOptional_rejects_partial_log_settings_without_exposing_values()
    {
        const string privateUser = "private-logs-user";

        var action = () => GrafanaLogsApiOptions.CreateOptional(
            "https://logs.example.test",
            privateUser,
            null,
            CreateMetricsOptions());

        var exception = action.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("requires URL, user, and token together");
        exception.ToString().Should().NotContain(privateUser);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static GrafanaLogsApiOptions CreateOptions(
        int maximumResponseBytes = 1024 * 1024,
        int maximumLines = 100) =>
        new(
            new Uri("https://logs.example.test/base"),
            "logs-user",
            "sensitive-logs-token",
            "readiness-job",
            "probe-a",
            "production",
            "primary",
            "v2-4-shadow-001",
            TimeSpan.FromSeconds(5),
            maximumResponseBytes,
            maximumLines,
            TimeSpan.FromSeconds(10));

    private static GrafanaMetricsApiOptions CreateMetricsOptions() =>
        new(
            new Uri("https://metrics.example.test/prometheus"),
            "metrics-user",
            "metrics-token",
            "readiness-job",
            "probe-a",
            "production",
            "primary",
            "v2-4-shadow-001",
            "region-a",
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5),
            1024 * 1024);

    private static string CreateLokiResponse(
        params (DateTimeOffset ObservedAtUtc, string Line)[] values) =>
        JsonSerializer.Serialize(new
        {
            status = "success",
            data = new
            {
                resultType = "streams",
                result = new[]
                {
                    new
                    {
                        stream = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["check_name"] = "http",
                        },
                        values = values.Select(value => new[]
                        {
                            ToUnixNanoseconds(value.ObservedAtUtc),
                            value.Line,
                        }),
                    },
                },
            },
        });

    private static string ToUnixNanoseconds(DateTimeOffset value) =>
        checked(value.ToUnixTimeMilliseconds() * 1_000_000)
            .ToString(CultureInfo.InvariantCulture);

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<StubResponse> _responses;

        public CapturingHttpMessageHandler(params StubResponse[] responses)
        {
            _responses = new Queue<StubResponse>(responses);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("The request URI is missing."),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No stub response remains.");
            }

            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record StubResponse(HttpStatusCode StatusCode, string Body);

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri RequestUri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);
}
