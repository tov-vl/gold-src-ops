using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class OperatorApiClientTests
{
    [Fact]
    public async Task QueueSayAsync_posts_only_the_say_contract()
    {
        var serverId = Guid.Parse("755b406e-f627-4c79-85c0-521e4cebe9b0");
        const string message = "Maintenance begins in five minutes";
        var capture = new CaptureHandler(HttpStatusCode.Created);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueSayAsync(serverId, message);

        result.Should().Be(OperatorCommandQueueResult.Queued);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/commands/say"));
        capture.Request.Should().Be(new SayCommandRequest(message));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorCommandQueueResult.ServerNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorCommandQueueResult.MissingRconCredential)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorCommandQueueResult.Rejected)]
    public async Task QueueSayAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new CaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.QueueSayAsync(Guid.NewGuid(), "Message");

        result.Should().Be((OperatorCommandQueueResult)expected);
    }

    [Fact]
    public async Task QueueSayAsync_rejects_an_unexpected_status()
    {
        var capture = new CaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.QueueSayAsync(Guid.NewGuid(), "Message");

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.example.test/")
    };

    private sealed class CaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public SayCommandRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Request = await request.Content!.ReadFromJsonAsync<SayCommandRequest>(cancellationToken);
            return new HttpResponseMessage(responseStatusCode);
        }
    }
}
