using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class PublicStatusDeadlineTests
{
    [Theory]
    [InlineData("status", false)]
    [InlineData("history", false)]
    [InlineData("leaderboard", false)]
    [InlineData("server", false)]
    [InlineData("status", true)]
    public async Task Complete_response_deadline_cancels_missing_or_partial_body(string operation, bool partialBody)
    {
        using var stream = new StalledStream(partialBody);
        using var handler = new DelayedHandler(TimeSpan.Zero, stream);
        using var http = CreateHttpClient(handler, TimeSpan.FromMilliseconds(200));
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        var client = new PublicStatusClient(http, metrics);

        var action = () => InvokeAsync(client, operation).WaitAsync(TimeSpan.FromSeconds(5));

        var exception = await action.Should().ThrowAsync<TaskCanceledException>();
        exception.Which.InnerException.Should().BeOfType<TimeoutException>();
        capture.Values.Should().ContainSingle().Which.Tags["outcome"].Should().Be("timeout");
        capture.Values.Single().Seconds.Should().BeGreaterThan(0.1);
        stream.CancellationObserved.Should().BeTrue();
        stream.Disposed.Should().BeTrue();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task Header_wait_and_body_read_share_one_budget()
    {
        using var stream = new StalledStream(false);
        using var handler = new DelayedHandler(TimeSpan.FromMilliseconds(1200), stream);
        using var http = CreateHttpClient(handler, TimeSpan.FromSeconds(2));
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        var client = new PublicStatusClient(http, metrics);
        var elapsed = Stopwatch.StartNew();

        var action = () => client.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await action.Should().ThrowAsync<TaskCanceledException>();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(2800));
        capture.Values.Should().ContainSingle().Which.Tags["outcome"].Should().Be("timeout");
        capture.Values.Single().Seconds.Should().BeGreaterThan(0.1);
        stream.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public async Task Deadline_also_cancels_waiting_for_headers()
    {
        using var stream = new StalledStream(false);
        using var handler = new DelayedHandler(Timeout.InfiniteTimeSpan, stream);
        using var http = CreateHttpClient(handler, TimeSpan.FromMilliseconds(200));
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        var client = new PublicStatusClient(http, metrics);

        var action = () => client.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await action.Should().ThrowAsync<TaskCanceledException>();
        capture.Values.Should().ContainSingle().Which.Tags["outcome"].Should().Be("timeout");
        handler.HeaderCancellationObserved.Should().BeTrue();
        handler.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_can_cancel_before_the_deadline(bool duringBody)
    {
        using var stream = new StalledStream(false);
        using var handler = new DelayedHandler(duringBody ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, stream);
        using var http = CreateHttpClient(handler, TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        var client = new PublicStatusClient(http, metrics);
        var pending = client.GetStatusAsync(caller.Token);
        await (duringBody ? stream.ReadStarted.Task : handler.RequestStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));

        caller.Cancel();
        var action = async () => await pending.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await action.Should().ThrowAsync<OperationCanceledException>();
        capture.Values.Should().ContainSingle().Which.Tags["outcome"].Should().Be("canceled");
        (exception.Which.InnerException is TimeoutException).Should().BeFalse();
        (duringBody ? stream.CancellationObserved : handler.HeaderCancellationObserved).Should().BeTrue();
        handler.RequestCount.Should().Be(1);
    }

    [Theory]
    [InlineData("leaderboard")]
    [InlineData("server")]
    public async Task Not_found_does_not_wait_for_a_response_body(string operation)
    {
        using var stream = new StalledStream(false);
        using var handler = new DelayedHandler(TimeSpan.Zero, stream, HttpStatusCode.NotFound);
        using var http = CreateHttpClient(handler, TimeSpan.FromSeconds(1));
        using var metrics = new PublicApiMetrics();
        using var capture = new PublicApiMeasurementCapture(metrics);
        var client = new PublicStatusClient(http, metrics);

        object? result = string.Equals(operation, "leaderboard", StringComparison.Ordinal)
            ? await client.GetLeaderboardAsync()
            : await client.GetServerJoinAsync();

        capture.Values.Should().ContainSingle().Which.Tags["outcome"].Should().Be("not_found");
        result.Should().BeNull();
        stream.ReadStarted.Task.IsCompleted.Should().BeFalse();
        stream.Disposed.Should().BeTrue();
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, TimeSpan timeout) => new(handler)
    {
        BaseAddress = new Uri("https://api.example.test/"),
        Timeout = timeout
    };

    private static Task InvokeAsync(PublicStatusClient client, string operation) => operation switch
    {
        "status" => client.GetStatusAsync(),
        "history" => client.GetA2sHistoryAsync(PublicStatusClient.Last24HoursWindow),
        "leaderboard" => client.GetLeaderboardAsync(),
        "server" => client.GetServerJoinAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private sealed class DelayedHandler(TimeSpan headerDelay, Stream body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        public bool HeaderCancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestStarted.TrySetResult();
            try
            {
                await Task.Delay(headerDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                HeaderCancellationObserved = true;
                throw;
            }

            return new HttpResponseMessage(status) { Content = new StreamContent(body) };
        }
    }

    private sealed class StalledStream(bool partialBody) : MemoryStream
    {
        private bool prefixWritten;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            if (partialBody && !prefixWritten)
            {
                buffer.Span[0] = (byte)'{';
                prefixWritten = true;
                return 1;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                // Network streams may report the base exception, not TaskCanceledException.
                throw new OperationCanceledException(cancellationToken);
            }

            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
