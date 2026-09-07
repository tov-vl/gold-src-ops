using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class PublicStatusClientTests
{
    [Theory]
    [InlineData(PublicStatusClient.Last24HoursWindow)]
    [InlineData(PublicStatusClient.Last7DaysWindow)]
    public async Task GetA2sHistoryAsync_requests_the_selected_supported_window(string window)
    {
        var expected = CreateHistory(window);
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected)
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new PublicStatusClient(httpClient);

        var result = await client.GetA2sHistoryAsync(window);

        result.Should().BeEquivalentTo(expected);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/public/a2s-history?window={window}"));
    }

    [Fact]
    public async Task GetA2sHistoryAsync_rejects_an_unsupported_window_without_sending_a_request()
    {
        var capture = new CaptureHandler(_ => throw new InvalidOperationException("A request was not expected."));
        using var httpClient = CreateHttpClient(capture);
        var client = new PublicStatusClient(httpClient);

        var action = () => client.GetA2sHistoryAsync("30d");

        await action.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("window");
        capture.RequestUri.Should().BeNull();
    }

    [Fact]
    public async Task GetA2sHistoryAsync_rejects_an_empty_success_response()
    {
        var capture = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        });
        using var httpClient = CreateHttpClient(capture);
        var client = new PublicStatusClient(httpClient);

        var action = () => client.GetA2sHistoryAsync(PublicStatusClient.Last24HoursWindow);

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*A2S history response was empty*");
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.example.test/")
    };

    private static PublicA2sHistoryResponse CreateHistory(string window)
    {
        var toUtc = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        var isSevenDays = string.Equals(
            window,
            PublicStatusClient.Last7DaysWindow,
            StringComparison.Ordinal);
        var bucketMinutes = isSevenDays ? 360 : 60;
        var totalBuckets = isSevenDays ? 28 : 24;
        var fromUtc = toUtc.AddMinutes(-bucketMinutes * totalBuckets);
        return new PublicA2sHistoryResponse(
            window,
            fromUtc,
            toUtc,
            bucketMinutes,
            ObservedBuckets: 1,
            totalBuckets,
            ObservedReachabilityPercent: 100m,
            [new PublicA2sBucketResponse(fromUtc, "operational", 100m)]);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(responseFactory(request));
        }
    }
}
