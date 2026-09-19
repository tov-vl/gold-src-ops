using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.ProviderDelivery;

namespace GoldSrcOps.AlertReceiver.Tests.ProviderDelivery;

public sealed class HttpProviderDeliveryChannelTests
{
    [Fact]
    public async Task Successful_delivery_sends_stable_identity_and_provider_neutral_envelope()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var channel = CreateChannel(handler);
        var message = CreateMessage();

        var result = await channel.DeliverAsync(message, CancellationToken.None);

        result.Kind.Should().Be(ProviderDeliveryResultKind.Delivered);
        handler.RequestCount.Should().Be(1);
        handler.Authorization.Should().Be("Bearer test-secret");
        handler.IdempotencyKey.Should().Be(message.SourceEventId.ToString("D"));
        handler.ContentType.Should().Be("application/json");
        handler.ContentLength.Should().BeGreaterThan(0);
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("action").GetString().Should().Be("Trigger");
        body.RootElement.GetProperty("groupKey").GetString().Should().Be(
            $"goldsrcops-availability:{message.IncidentId:D}");
        body.RootElement.GetProperty("sourceEventId").GetGuid().Should().Be(message.SourceEventId);
        body.RootElement.GetProperty("payload").GetProperty("eventType").GetString()
            .Should().Be("server.unavailable.v1");
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, "RetryableFailure")]
    [InlineData(HttpStatusCode.TooManyRequests, "RetryableFailure")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "RetryableFailure")]
    [InlineData(HttpStatusCode.BadRequest, "PermanentFailure")]
    [InlineData(HttpStatusCode.Redirect, "PermanentFailure")]
    public async Task Response_status_is_classified_without_following_redirects(
        HttpStatusCode statusCode,
        string expected)
    {
        var handler = new RecordingHandler(statusCode);
        using var channel = CreateChannel(handler);

        var result = await channel.DeliverAsync(CreateMessage(), CancellationToken.None);

        result.Kind.ToString().Should().Be(expected);
        handler.RequestCount.Should().Be(1);
    }

    private static HttpProviderDeliveryChannel CreateChannel(HttpMessageHandler handler)
    {
        var options = new ProviderDeliveryOptions
        {
            Enabled = true,
            Endpoint = "https://provider.invalid/deliver",
            Authorization = "Bearer test-secret",
            RequestTimeout = TimeSpan.FromSeconds(2),
            MaximumRetryAfter = TimeSpan.FromMinutes(1),
        };
        return new HttpProviderDeliveryChannel(
            options,
            TimeProvider.System,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    private static ClaimedProviderOutboxMessage CreateMessage()
    {
        using var payload = JsonDocument.Parse("""{"eventType":"server.unavailable.v1"}""");
        return new ClaimedProviderOutboxMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Trigger",
            DateTimeOffset.UtcNow,
            payload.RootElement.Clone(),
            AttemptCount: 1,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? Authorization { get; private set; }

        public string? IdempotencyKey { get; private set; }

        public string? ContentType { get; private set; }

        public long? ContentLength { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Authorization = request.Headers.Authorization?.ToString();
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            ContentType = request.Content!.Headers.ContentType?.ToString();
            ContentLength = request.Content.Headers.ContentLength;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode);
        }
    }
}
