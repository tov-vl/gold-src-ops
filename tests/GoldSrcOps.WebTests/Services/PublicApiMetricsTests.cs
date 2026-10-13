using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class PublicApiMetricsTests
{
    [Theory]
    [InlineData("status", 200, "{}", "success")]
    [InlineData("history", 200, "{}", "success")]
    [InlineData("leaderboard", 200, "{}", "success")]
    [InlineData("server", 200, "{}", "success")]
    [InlineData("server", 404, "", "not_found")]
    [InlineData("leaderboard", 404, "", "not_found")]
    [InlineData("status", 404, "", "error")]
    [InlineData("history", 503, "", "error")]
    [InlineData("leaderboard", 200, "null", "error")]
    [InlineData("server", 200, "{invalid-json", "error")]
    public async Task Each_read_records_one_bounded_result(string operation, int status, string body, string outcome)
    {
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        using var handler = new FixtureHandler((HttpStatusCode)status, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://private.example.test/") };
        var client = new PublicStatusClient(http, metrics);

        var exception = await Record.ExceptionAsync(async () =>
        {
            switch (operation)
            {
                case "status": await client.GetStatusAsync(); break;
                case "history": await client.GetA2sHistoryAsync(PublicStatusClient.Last7DaysWindow); break;
                case "leaderboard": await client.GetLeaderboardAsync(); break;
                case "server": await client.GetServerJoinAsync(); break;
            }
        });

        (exception is not null).Should().Be(string.Equals(outcome, "error", StringComparison.Ordinal));
        var measurement = capture.Values.Should().ContainSingle().Subject;
        measurement.Seconds.Should().BeGreaterThan(0);
        measurement.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = operation,
            ["outcome"] = outcome
        });
    }

    [Fact]
    public async Task Invalid_window_does_not_count_as_an_API_request()
    {
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        using var http = new HttpClient();
        var client = new PublicStatusClient(http, metrics);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetA2sHistoryAsync("invalid"));

        capture.Values.Should().BeEmpty();
    }

    private sealed class FixtureHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
