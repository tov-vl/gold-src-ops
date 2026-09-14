using System.Net;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class ClientCredentialsAccessTokenProviderTests : IDisposable
{
    private readonly string _directoryPath = Path.Combine(
        Path.GetTempPath(),
        "goldsrcops-game-event-agent-token-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetAccessTokenAsync_caches_token_until_invalidated()
    {
        Directory.CreateDirectory(_directoryPath);
        var secretPath = Path.Combine(_directoryPath, "client-secret");
        await WriteSecretAsync(secretPath);
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(StubHttpMessageHandler.JsonResponse(
                HttpStatusCode.OK,
                """
                {
                  "access_token": "sandbox-token",
                  "token_type": "Bearer",
                  "expires_in": 3600,
                  "scope": "ingest:game-events"
                }
                """)));
        var options = new OAuthClientCredentialsOptions(
            new Uri("https://identity.example.test/oauth/token"),
            "sandbox-client",
            secretPath,
            "https://api.example.test",
            "ingest:game-events",
            TimeSpan.FromSeconds(60));
        using var sut = new ClientCredentialsAccessTokenProvider(
            new StubHttpClientFactory(handler),
            options,
            new TestTimeProvider(GameEventAgentTestData.NowUtc));

        var first = await sut.GetAccessTokenAsync(CancellationToken.None);
        var cached = await sut.GetAccessTokenAsync(CancellationToken.None);
        sut.Invalidate();
        var refreshed = await sut.GetAccessTokenAsync(CancellationToken.None);

        first.Should().Be("sandbox-token");
        cached.Should().Be(first);
        refreshed.Should().Be(first);
        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_reports_sanitized_error_for_failed_endpoint()
    {
        Directory.CreateDirectory(_directoryPath);
        var secretPath = Path.Combine(_directoryPath, "client-secret");
        await WriteSecretAsync(secretPath);
        using var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var options = new OAuthClientCredentialsOptions(
            new Uri("https://identity.example.test/oauth/token"),
            "sandbox-client",
            secretPath,
            "https://api.example.test",
            "ingest:game-events",
            TimeSpan.FromSeconds(60));
        using var sut = new ClientCredentialsAccessTokenProvider(
            new StubHttpClientFactory(handler),
            options,
            new TestTimeProvider(GameEventAgentTestData.NowUtc));

        var act = async () => await sut.GetAccessTokenAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<GameEventTokenException>())
            .WithMessage("*HTTP 401*")
            .Which.Message.Should().NotContain("sandbox-secret-value");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }

    private static async Task WriteSecretAsync(string path)
    {
        await File.WriteAllTextAsync(path, "sandbox-secret-value");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
