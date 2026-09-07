using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoldSrcOps.WebTests.Pages;

public sealed class PublicDashboardIntegrationTests
{
    [Theory]
    [InlineData("/", PublicStatusClient.Last24HoursWindow, 24, 23, "Hourly buckets")]
    [InlineData("/?range=7d", PublicStatusClient.Last7DaysWindow, 28, 27, "Six-hour buckets")]
    public async Task Public_dashboard_renders_sanitized_A2s_history_for_the_selected_window(
        string requestPath,
        string expectedWindow,
        int expectedBuckets,
        int expectedObservedBuckets,
        string expectedBucketLabel)
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(requestPath);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Handler.LastHistoryWindow.Should().Be(expectedWindow);
        body.Should().Contain("A2S reachability history");
        body.Should().Contain("99.5%");
        body.Should().Contain($"{expectedObservedBuckets} of {expectedBuckets} buckets");
        body.Should().Contain(expectedBucketLabel);
        body.Should().Contain("not API uptime");
        body.Should().NotContain(PublicDashboardWebApplicationFactory.PrivateDataSentinel);
        (body.Split("history-bar history-bar--", StringSplitOptions.None).Length - 1)
            .Should().Be(expectedBuckets);
    }

    [Fact]
    public async Task Public_dashboard_defaults_an_untrusted_range_to_the_bounded_24_hour_window()
    {
        await using var factory = new PublicDashboardWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/?range=https%3A%2F%2Fprivate.example%2Fsecret");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Handler.LastHistoryWindow.Should().Be(PublicStatusClient.Last24HoursWindow);
        body.Should().Contain("Hourly buckets");
        body.Should().NotContain("private.example");
    }

    [Fact]
    public async Task Public_dashboard_keeps_history_visible_when_current_status_is_unavailable()
    {
        await using var factory = new PublicDashboardWebApplicationFactory(statusUnavailable: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("Monitoring data could not be reached");
        body.Should().Contain("A2S reachability history");
        body.Should().Contain("99.5%");
    }
}

internal sealed class PublicDashboardWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string PrivateDataSentinel = "private-server-data-must-not-render";

    private readonly bool statusUnavailable;

    public PublicDashboardWebApplicationFactory(bool statusUnavailable = false)
    {
        this.statusUnavailable = statusUnavailable;
    }

    public FixturePublicStatusHandler Handler { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["GoldSrcOpsApi:BaseUrl"] = "https://api.example.test/",
                    ["Authentication:Enabled"] = "false"
                });
        });
        builder.ConfigureServices(services =>
        {
            Handler = new FixturePublicStatusHandler(statusUnavailable);
            var httpClient = new HttpClient(Handler)
            {
                BaseAddress = new Uri("https://api.example.test/")
            };
            services.RemoveAll<PublicStatusClient>();
            services.AddSingleton(new PublicStatusClient(httpClient));
        });
    }

    internal sealed class FixturePublicStatusHandler(bool statusUnavailable) : HttpMessageHandler
    {
        public string? LastHistoryWindow { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri;
            if (string.Equals(
                requestUri?.AbsolutePath,
                "/api/public/status",
                StringComparison.Ordinal))
            {
                return Task.FromResult(statusUnavailable
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : JsonResponse(new PublicStatusResponse(
                        "operational",
                        MonitoredServers: 1,
                        OnlineServers: 1,
                        ServersRequiringAttention: 0,
                        OpenIncidents: 0,
                        LastObservedAtUtc: new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero))));
            }

            if (requestUri is not null && string.Equals(
                requestUri.AbsolutePath,
                "/api/public/a2s-history",
                StringComparison.Ordinal))
            {
                LastHistoryWindow = GetQueryValue(requestUri.Query, "window");
                return Task.FromResult(JsonResponse(CreateHistory(LastHistoryWindow)));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value)
        };

        private static PublicA2sHistoryResponse CreateHistory(string? window)
        {
            var isSevenDays = string.Equals(
                window,
                PublicStatusClient.Last7DaysWindow,
                StringComparison.Ordinal);
            var bucketMinutes = isSevenDays ? 360 : 60;
            var totalBuckets = isSevenDays ? 28 : 24;
            var toUtc = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
            var fromUtc = toUtc.AddMinutes(-bucketMinutes * totalBuckets);
            var buckets = Enumerable.Range(0, totalBuckets)
                .Select(index => index == 0
                    ? new PublicA2sBucketResponse(
                        fromUtc,
                        "unknown",
                        ObservedReachabilityPercent: null)
                    : new PublicA2sBucketResponse(
                        fromUtc.AddMinutes(bucketMinutes * index),
                        index == totalBuckets - 1 ? "degraded" : "operational",
                        index == totalBuckets - 1 ? 88m : 100m))
                .ToArray();

            return new PublicA2sHistoryResponse(
                window ?? PublicStatusClient.Last24HoursWindow,
                fromUtc,
                toUtc,
                bucketMinutes,
                ObservedBuckets: totalBuckets - 1,
                totalBuckets,
                ObservedReachabilityPercent: 99.5m,
                buckets);
        }

        private static string? GetQueryValue(string query, string key)
        {
            foreach (var item in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = item.Split('=', count: 2);
                if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return null;
        }
    }
}
