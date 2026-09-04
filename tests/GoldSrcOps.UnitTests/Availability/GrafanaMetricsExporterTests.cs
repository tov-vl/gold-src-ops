using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Availability;
using GoldSrcOps.AvailabilityExporter;

namespace GoldSrcOps.UnitTests.Availability;

public sealed class GrafanaMetricsExporterTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportAsync_correlates_source_timestamps_and_deduplicates_stale_range_samples()
    {
        var sourceTimestamp = WindowStart.AddSeconds(10);
        var firstEvaluation = WindowStart.AddSeconds(15);
        var secondEvaluation = WindowStart.AddSeconds(30);
        using var handler = new SequenceHttpMessageHandler(
            CreateMatrix((firstEvaluation, 1d), (secondEvaluation, 1d)),
            CreateMatrix((firstEvaluation, UnixSeconds(sourceTimestamp)), (secondEvaluation, UnixSeconds(sourceTimestamp))),
            CreateMatrix((firstEvaluation, 200d), (secondEvaluation, 200d)),
            CreateMatrix((firstEvaluation, UnixSeconds(sourceTimestamp)), (secondEvaluation, UnixSeconds(sourceTimestamp))),
            CreateMatrix((firstEvaluation, 0.25d), (secondEvaluation, 0.25d)),
            CreateMatrix((firstEvaluation, UnixSeconds(sourceTimestamp)), (secondEvaluation, UnixSeconds(sourceTimestamp))));
        using var httpClient = CreateHttpClient(handler);
        var options = CreateOptions();
        var exporter = new GrafanaMetricsExporter(
            new GrafanaMetricsApiClient(httpClient, options),
            options);

        var records = await exporter.ExportAsync(
            WindowStart,
            WindowStart.AddMinutes(1),
            CancellationToken.None);

        var record = records.Should().ContainSingle().Which;
        record.Outcome.Should().Be(AvailabilityOutcome.Good);
        record.HttpStatus.Should().Be(200);
        record.DurationMilliseconds.Should().Be(250);
        record.CompletedAtUtc.Should().Be(sourceTimestamp);
        record.StartedAtUtc.Should().Be(sourceTimestamp.AddMilliseconds(-250));

        handler.Requests.Should().HaveCount(6);
        foreach (var request in handler.Requests)
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri.Should().EndWith("/api/v1/query_range");
            request.RequestUri.Should().NotContain("sensitive-token-value");
            request.Body.Should().NotContain("sensitive-token-value");
            request.AuthorizationScheme.Should().Be("Basic");
            request.AuthorizationParameter.Should().NotBeNullOrWhiteSpace();
        }

        var decodedQueries = Uri.UnescapeDataString(string.Join("&", handler.Requests.Select(request => request.Body)));
        decodedQueries.Should().Contain("label_environment=\"production\"");
        decodedQueries.Should().Contain("label_role=\"primary\"");
        decodedQueries.Should().Contain("label_monitor_revision=\"v2-4-shadow-001\"");
        decodedQueries.Should().Contain("timestamp(probe_success");
    }

    [Fact]
    public async Task ExportAsync_does_not_attach_stale_supporting_metrics_to_a_failed_execution()
    {
        var currentSource = WindowStart.AddSeconds(20);
        var staleSource = WindowStart.AddSeconds(10);
        var evaluation = WindowStart.AddSeconds(30);
        using var handler = new SequenceHttpMessageHandler(
            CreateMatrix((evaluation, 0d)),
            CreateMatrix((evaluation, UnixSeconds(currentSource))),
            CreateMatrix((evaluation, 503d)),
            CreateMatrix((evaluation, UnixSeconds(staleSource))),
            CreateEmptyMatrix(),
            CreateEmptyMatrix());
        using var httpClient = CreateHttpClient(handler);
        var options = CreateOptions();
        var exporter = new GrafanaMetricsExporter(
            new GrafanaMetricsApiClient(httpClient, options),
            options);

        var records = await exporter.ExportAsync(
            WindowStart,
            WindowStart.AddMinutes(1),
            CancellationToken.None);

        var record = records.Should().ContainSingle().Which;
        record.Outcome.Should().Be(AvailabilityOutcome.MonitorError);
        record.HttpStatus.Should().BeNull();
        record.DurationMilliseconds.Should().BeNull();
    }

    [Fact]
    public async Task QueryRangeAsync_does_not_include_response_content_in_an_error()
    {
        using var handler = new SequenceHttpMessageHandler(
            new StubResponse(HttpStatusCode.InternalServerError, "private-response-payload"));
        using var httpClient = CreateHttpClient(handler);
        var client = new GrafanaMetricsApiClient(httpClient, CreateOptions());

        var exception = await Assert.ThrowsAsync<GrafanaMetricsApiException>(() =>
            client.QueryRangeAsync(
                "up",
                WindowStart,
                WindowStart.AddMinutes(1),
                CancellationToken.None));

        exception.Message.Should().Contain("HTTP status 500");
        exception.Message.Should().NotContain("private-response-payload");
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static GrafanaMetricsApiOptions CreateOptions() =>
        new(
            new Uri("https://metrics.example.test/prometheus"),
            "metrics-user",
            "sensitive-token-value",
            "readiness-job",
            "probe-a",
            "production",
            "primary",
            "v2-4-shadow-001",
            "region-a",
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5),
            1024 * 1024);

    private static string CreateEmptyMatrix() => CreateMatrix();

    private static string CreateMatrix(params (DateTimeOffset EvaluatedAtUtc, double Value)[] samples)
    {
        object[] result = samples.Length == 0
            ? []
            :
            [
                new
                {
                    metric = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["job"] = "readiness-job",
                        ["instance"] = "https://public.example.test/health/ready",
                        ["probe"] = "probe-a",
                        ["config_version"] = "1",
                    },
                    values = samples.Select(sample => new object[]
                    {
                        UnixSeconds(sample.EvaluatedAtUtc),
                        sample.Value.ToString("R", CultureInfo.InvariantCulture),
                    }),
                },
            ];

        return JsonSerializer.Serialize(new
        {
            status = "success",
            data = new
            {
                resultType = "matrix",
                result,
            },
        });
    }

    private static double UnixSeconds(DateTimeOffset value) => value.ToUnixTimeMilliseconds() / 1000d;

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<StubResponse> _responses;

        public SequenceHttpMessageHandler(params string[] responses)
            : this(responses.Select(response => new StubResponse(HttpStatusCode.OK, response)).ToArray())
        {
        }

        public SequenceHttpMessageHandler(params StubResponse[] responses)
        {
            _responses = new Queue<StubResponse>(responses);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No stub response remains.");
            }

            return new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record StubResponse(HttpStatusCode StatusCode, string Body);

    private sealed record CapturedRequest(
        HttpMethod Method,
        string RequestUri,
        string Body,
        string? AuthorizationScheme,
        string? AuthorizationParameter);
}
