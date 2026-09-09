using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
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

    [Theory]
    [InlineData(true, "enable")]
    [InlineData(false, "disable")]
    public async Task SetMonitoringEnabledAsync_posts_only_the_requested_lifecycle_action(
        bool enabled,
        string actionSegment)
    {
        var serverId = Guid.Parse("3bb3ed44-2e10-442c-b1e1-62bb3f4a2b35");
        var capture = new LifecycleCaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetMonitoringEnabledAsync(serverId, enabled);

        result.Should().Be(OperatorMonitoringUpdateResult.Updated);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/servers/{serverId:D}/{actionSegment}"));
        capture.HasContent.Should().BeFalse();
    }

    [Fact]
    public async Task SetMonitoringEnabledAsync_maps_a_missing_server()
    {
        var capture = new LifecycleCaptureHandler(HttpStatusCode.NotFound);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.SetMonitoringEnabledAsync(Guid.NewGuid(), enabled: true);

        result.Should().Be(OperatorMonitoringUpdateResult.ServerNotFound);
    }

    [Fact]
    public async Task SetMonitoringEnabledAsync_rejects_an_unexpected_status()
    {
        var capture = new LifecycleCaptureHandler(HttpStatusCode.Accepted);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.SetMonitoringEnabledAsync(Guid.NewGuid(), enabled: false);

        var exception = await action.Should().ThrowAsync<HttpRequestException>();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_posts_reason_and_idempotency_key()
    {
        var eventId = Guid.Parse("419bb150-112f-4f58-a6bf-162ca41a0895");
        var requestId = Guid.Parse("418e3f0f-b52f-494f-99de-06b2b37e43ad");
        const string reason = "Receiver health was verified";
        var capture = new ReplayCaptureHandler(HttpStatusCode.Accepted);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.ReplayDeadLetterAsync(eventId, requestId, reason);

        result.Should().Be(OperatorReplayResult.Accepted);
        capture.Method.Should().Be(HttpMethod.Post);
        capture.RequestUri.Should().Be(
            new Uri($"https://api.example.test/api/alert-delivery/dead-letters/{eventId:D}/replay"));
        capture.IdempotencyKey.Should().Be(requestId.ToString("D"));
        capture.Request.Should().Be(new ReplayDeadLetterRequest(reason));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, (int)OperatorReplayResult.EventNotFound)]
    [InlineData(HttpStatusCode.Conflict, (int)OperatorReplayResult.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, (int)OperatorReplayResult.Rejected)]
    public async Task ReplayDeadLetterAsync_maps_expected_rejections(
        HttpStatusCode statusCode,
        int expected)
    {
        var capture = new ReplayCaptureHandler(statusCode);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var result = await client.ReplayDeadLetterAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Receiver recovered");

        result.Should().Be((OperatorReplayResult)expected);
    }

    [Fact]
    public async Task ReplayDeadLetterAsync_rejects_an_unexpected_status()
    {
        var capture = new ReplayCaptureHandler(HttpStatusCode.OK);
        using var httpClient = CreateHttpClient(capture);
        var client = new OperatorApiClient(httpClient);

        var action = () => client.ReplayDeadLetterAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Receiver recovered");

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

    private sealed class ReplayCaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public string? IdempotencyKey { get; private set; }

        public ReplayDeadLetterRequest? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            Request = await request.Content!.ReadFromJsonAsync<ReplayDeadLetterRequest>(cancellationToken);
            return new HttpResponseMessage(responseStatusCode);
        }
    }

    private sealed class LifecycleCaptureHandler(HttpStatusCode responseStatusCode) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public bool HasContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            HasContent = request.Content is not null;
            return Task.FromResult(new HttpResponseMessage(responseStatusCode));
        }
    }
}
