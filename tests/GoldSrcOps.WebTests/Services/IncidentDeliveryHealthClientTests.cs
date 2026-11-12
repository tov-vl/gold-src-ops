using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class IncidentDeliveryHealthClientTests
{
    [Fact]
    public async Task Adapter_reuses_only_existing_read_endpoints_and_keeps_authorization_on_the_server()
    {
        using var handler = new StatusHandler();
        using var readerHttp = new HttpClient(handler, disposeHandler: false);
        using var receiverHttp = new HttpClient(handler, disposeHandler: false);
        readerHttp.BaseAddress = new Uri("https://api.example.test/");
        receiverHttp.BaseAddress = new Uri("https://receiver.example.test/");
        readerHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "reader-sentinel");
        receiverHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "receiver-sentinel");
        var adapter = new IncidentDeliveryHealthClient(new ReaderApiClient(readerHttp), new ProviderOperationsClient(receiverHttp));
        adapter.IsReceiverConfigured.Should().BeTrue();
        var outbox = await adapter.ReadOutboxAsync(CancellationToken.None);
        var receiver = await adapter.ReadReceiverAsync(CancellationToken.None);
        outbox.Should().Be(OperatorHealthOverviewFixtures.Outbox(OperatorHealthOverviewFixtures.Now));
        receiver.Should().Be(OperatorHealthOverviewFixtures.Receiver(OperatorHealthOverviewFixtures.Now));
        handler.Requests.Should().Equal("GET /api/alert-delivery/status reader-sentinel", "GET /internal/v1/provider-delivery/status receiver-sentinel");
    }

    [Fact]
    public void Adapter_preserves_disabled_receiver_boundary()
    {
        using var http = new HttpClient();
        var adapter = new IncidentDeliveryHealthClient(new ReaderApiClient(http), new DisabledProviderOperationsClient());
        adapter.IsReceiverConfigured.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Body_read_is_cancelled_by_its_budget_or_the_caller(bool receiver, bool callerCancels)
    {
        await using var body = new StalledBody();
        using var handler = new BodyHandler(body);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://fixture.example.test/");
        http.Timeout = Timeout.InfiniteTimeSpan;
        var adapter = new IncidentDeliveryHealthClient(new ReaderApiClient(http), new ProviderOperationsClient(http));
        using var cancellation = new CancellationTokenSource();
        Task pending = receiver ? adapter.ReadReceiverAsync(cancellation.Token) : adapter.ReadOutboxAsync(cancellation.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation.Token);
        if (callerCancels)
        {
            await cancellation.CancelAsync();
        }
        // Observe the result independently after the caller token may already be cancelled.
        var observe = async () => await pending.WaitAsync(TimeSpan.FromSeconds(15));
        await observe.Should().ThrowAsync<OperationCanceledException>();
        body.Cancelled.Should().BeTrue();
        handler.Reads.Should().Be(1);
    }

    private sealed class BodyHandler(Stream body) : HttpMessageHandler
    {
        public int Reads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }
    }

    private sealed class StalledBody : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            return 0;
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path + " " + request.Headers.Authorization?.Parameter);
            object value = path switch
            {
                "/api/alert-delivery/status" => OperatorHealthOverviewFixtures.Outbox(OperatorHealthOverviewFixtures.Now),
                "/internal/v1/provider-delivery/status" => OperatorHealthOverviewFixtures.Receiver(OperatorHealthOverviewFixtures.Now),
                _ => throw new InvalidOperationException("Unexpected endpoint")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
        }
    }
}
