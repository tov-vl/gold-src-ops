using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Infrastructure.Alerts;
using Microsoft.AspNetCore.Http;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class HttpWebhookAlertDeliveryChannelTests
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultMaximumRetryAfter = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(StatusCodes.Status200OK, AlertDeliveryAttemptResultKind.Delivered)]
    [InlineData(StatusCodes.Status204NoContent, AlertDeliveryAttemptResultKind.Delivered)]
    [InlineData(StatusCodes.Status302Found, AlertDeliveryAttemptResultKind.PermanentFailure)]
    [InlineData(StatusCodes.Status400BadRequest, AlertDeliveryAttemptResultKind.PermanentFailure)]
    [InlineData(StatusCodes.Status408RequestTimeout, AlertDeliveryAttemptResultKind.RetryableFailure)]
    [InlineData(StatusCodes.Status429TooManyRequests, AlertDeliveryAttemptResultKind.RetryableFailure)]
    [InlineData(StatusCodes.Status500InternalServerError, AlertDeliveryAttemptResultKind.RetryableFailure)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, AlertDeliveryAttemptResultKind.RetryableFailure)]
    public async Task Classifies_remote_status_without_reading_a_response_body(
        int statusCode,
        AlertDeliveryAttemptResultKind expectedKind)
    {
        await using var server = await SyntheticWebhookServer.StartAsync(context =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });
        using var sut = CreateChannel(server.GetUri("/webhook"));

        var result = await sut.DeliverAsync(CreateMessage(), CancellationToken.None);

        result.Kind.Should().Be(expectedKind);
        result.RemoteStatusCode.Should().Be(
            expectedKind == AlertDeliveryAttemptResultKind.Delivered ? null : statusCode);
        result.FailureCategory.Should().Be(
            expectedKind == AlertDeliveryAttemptResultKind.Delivered
                ? null
                : AlertDeliveryFailureCategory.RemoteResponse);
    }

    [Fact]
    public async Task Sends_one_post_per_attempt_with_a_stable_idempotency_key()
    {
        var requests = new ConcurrentQueue<CapturedRequest>();
        await using var server = await SyntheticWebhookServer.StartAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            requests.Enqueue(new CapturedRequest(
                context.Request.Method,
                context.Request.Headers["Idempotency-Key"].ToString(),
                context.Request.Headers.Authorization.ToString(),
                context.Request.ContentType,
                body));
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });
        using var sut = CreateChannel(server.GetUri("/webhook"), authorization: "Bearer test-token");
        var message = CreateMessage();

        var firstResult = await sut.DeliverAsync(message, CancellationToken.None);
        var secondResult = await sut.DeliverAsync(message, CancellationToken.None);

        firstResult.Kind.Should().Be(AlertDeliveryAttemptResultKind.Delivered);
        secondResult.Kind.Should().Be(AlertDeliveryAttemptResultKind.Delivered);
        requests.Should().HaveCount(2);
        requests.Should().AllSatisfy(request =>
        {
            request.Method.Should().Be(HttpMethods.Post);
            request.IdempotencyKey.Should().Be(message.Id.ToString("D", CultureInfo.InvariantCulture));
            request.Authorization.Should().Be("Bearer test-token");
            request.ContentType.Should().Be("application/json; charset=utf-8");
            request.Body.Should().Be(message.Payload);
        });
    }

    [Fact]
    public async Task Classifies_an_aborted_connection_as_retryable()
    {
        await using var server = await SyntheticWebhookServer.StartAsync(context =>
        {
            context.Abort();
            return Task.CompletedTask;
        });
        using var sut = CreateChannel(server.GetUri("/webhook"));

        var result = await sut.DeliverAsync(CreateMessage(), CancellationToken.None);

        result.Kind.Should().Be(AlertDeliveryAttemptResultKind.RetryableFailure);
        result.FailureCategory.Should().Be(AlertDeliveryFailureCategory.Transport);
        result.RemoteStatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_follow_redirects()
    {
        var requestPaths = new ConcurrentQueue<string>();
        await using var server = await SyntheticWebhookServer.StartAsync(context =>
        {
            requestPaths.Enqueue(context.Request.Path.Value ?? string.Empty);
            if (context.Request.Path == "/webhook")
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = "/redirect-target";
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
            }

            return Task.CompletedTask;
        });
        using var sut = CreateChannel(server.GetUri("/webhook"));

        var result = await sut.DeliverAsync(CreateMessage(), CancellationToken.None);

        result.Kind.Should().Be(AlertDeliveryAttemptResultKind.PermanentFailure);
        result.RemoteStatusCode.Should().Be(StatusCodes.Status307TemporaryRedirect);
        requestPaths.Should().Equal("/webhook");
    }

    [Fact]
    public async Task Classifies_the_adapter_timeout_as_retryable()
    {
        await using var server = await SyntheticWebhookServer.StartAsync(async context =>
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted));
        using var sut = CreateChannel(
            server.GetUri("/webhook"),
            requestTimeout: TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        var result = await sut.DeliverAsync(CreateMessage(), CancellationToken.None);

        stopwatch.Stop();
        result.Kind.Should().Be(AlertDeliveryAttemptResultKind.RetryableFailure);
        result.FailureCategory.Should().Be(AlertDeliveryFailureCategory.Timeout);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Propagates_caller_cancellation()
    {
        await using var server = await SyntheticWebhookServer.StartAsync(async context =>
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted));
        using var sut = CreateChannel(server.GetUri("/webhook"));
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () =>
            await sut.DeliverAsync(CreateMessage(), cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Returns_only_valid_bounded_retry_after_values()
    {
        var requestCount = 0;
        await using var server = await SyntheticWebhookServer.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var retryAfterSeconds = Interlocked.Increment(ref requestCount) == 1 ? 5 : 120;
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
        using var sut = CreateChannel(
            server.GetUri("/webhook"),
            maximumRetryAfter: TimeSpan.FromSeconds(30));
        var message = CreateMessage();

        var bounded = await sut.DeliverAsync(message, CancellationToken.None);
        var excessive = await sut.DeliverAsync(message, CancellationToken.None);

        bounded.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
        excessive.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task Completes_after_headers_without_buffering_a_pending_response_body()
    {
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SyntheticWebhookServer.StartAsync(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentLength = 1_000_000_000;
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(new byte[] { 0 }, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await releaseBody.Task.WaitAsync(context.RequestAborted);
        });
        using var sut = CreateChannel(server.GetUri("/webhook"));

        try
        {
            var delivery = sut.DeliverAsync(CreateMessage(), CancellationToken.None);
            var completed = await Task.WhenAny(delivery, Task.Delay(TimeSpan.FromSeconds(3)));

            completed.Should().BeSameAs(delivery);
            var result = await delivery;
            result.Kind.Should().Be(AlertDeliveryAttemptResultKind.RetryableFailure);
            result.RemoteStatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            releaseBody.TrySetResult();
        }
    }

    private static HttpWebhookAlertDeliveryChannel CreateChannel(
        Uri endpoint,
        TimeSpan? requestTimeout = null,
        TimeSpan? maximumRetryAfter = null,
        string? authorization = null) =>
        new(new HttpWebhookDeliverySettings(
            endpoint,
            requestTimeout ?? DefaultRequestTimeout,
            maximumRetryAfter ?? DefaultMaximumRetryAfter,
            authorization));

    private static ClaimedOutboxMessage CreateMessage() =>
        new(
            Guid.NewGuid(),
            IncidentAlertEvents.ServerUnavailable,
            IncidentAlertEventV1.CurrentPayloadVersion,
            IncidentAlertEvents.AggregateType,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
            "{\"eventType\":\"server.availability.unavailable\"}",
            AttemptCount: 1,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 26, 12, 0, 1, TimeSpan.Zero));

    private sealed record CapturedRequest(
        string Method,
        string IdempotencyKey,
        string Authorization,
        string? ContentType,
        string Body);
}
