using System.Net;
using System.Text;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

internal sealed class TemporaryAgentDatabase : IDisposable
{
    private readonly string _directoryPath = Path.Combine(
        Path.GetTempPath(),
        "goldsrcops-game-event-agent-tests",
        Guid.NewGuid().ToString("N"));

    public string DatabasePath => Path.Combine(_directoryPath, "queue.db");

    public GameEventQueueOptions CreateOptions(int capacity = 100) =>
        new(DatabasePath, capacity, TimeSpan.FromSeconds(2), Pooling: false);

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }
}

internal sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class StubDeliveryClient(params GameEventDeliveryResult[] results)
    : IGameEventDeliveryClient
{
    private readonly Queue<GameEventDeliveryResult> _results = new(results);

    public List<QueuedGameEvent> SentEvents { get; } = [];

    public Task<GameEventDeliveryResult> SendAsync(
        QueuedGameEvent gameEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SentEvents.Add(gameEvent);
        return Task.FromResult(_results.Dequeue());
    }
}

internal sealed class StubAccessTokenProvider(string accessToken = "sandbox-access-token")
    : IGameEventAccessTokenProvider
{
    public int Invalidations { get; private set; }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(accessToken);
    }

    public void Invalidate() => Invalidations++;
}

internal sealed class StubHttpClientFactory(
    HttpMessageHandler handler,
    Uri? baseAddress = null)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = baseAddress
    };
}

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback)
    : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    public string? LastAuthorizationScheme { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    public byte[]? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
        LastRequestUri = request.RequestUri;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        return await callback(request, cancellationToken);
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}

internal static class GameEventAgentTestData
{
    public static readonly DateTimeOffset NowUtc =
        new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);

    public static GameEventSourceInput CreateInput() =>
        new("round.ended", NowUtc, "de_dust2", Players: 12, Bots: 0);

    public static GameEventDeliveryOptions CreateDeliveryOptions(
        int batchSize = 20,
        int maximumAttempts = 12,
        TimeSpan? maximumEventAge = null,
        TimeSpan? retryBaseDelay = null,
        TimeSpan? retryMaximumDelay = null) =>
        new(
            Guid.Parse("bd790c72-30f1-4dd9-9ec3-b79da8315227"),
            new Uri("https://api.example.test/"),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            batchSize,
            maximumAttempts,
            maximumEventAge ?? TimeSpan.FromDays(30),
            retryBaseDelay ?? TimeSpan.FromSeconds(5),
            retryMaximumDelay ?? TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(10),
            new OAuthClientCredentialsOptions(
                new Uri("https://identity.example.test/oauth/token"),
                "sandbox-client",
                Path.Combine(Path.GetTempPath(), "unused-game-event-agent-secret"),
                "https://api.example.test",
                "ingest:game-events",
                TimeSpan.FromSeconds(60)));
}
