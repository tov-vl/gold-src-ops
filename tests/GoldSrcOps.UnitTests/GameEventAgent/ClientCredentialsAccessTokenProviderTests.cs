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

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, false, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead, false, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead, true, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.GroupWrite, true, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead, true, false)]
    public void IsSecretFileModeAccepted_applies_only_the_read_only_systemd_exception(
        UnixFileMode mode,
        bool isSystemdManagedCredential,
        bool expected)
    {
        var result = ClientCredentialsAccessTokenProvider.IsSecretFileModeAccepted(
            mode,
            isSystemdManagedCredential);

        result.Should().Be(expected);
    }

    [Fact]
    public void IsSystemdManagedCredentialPath_accepts_only_an_immediate_file()
    {
        var credentialsDirectory = Path.Combine(_directoryPath, "credentials");
        var nestedDirectory = Path.Combine(credentialsDirectory, "nested");
        var siblingDirectory = Path.Combine(_directoryPath, "other");
        Directory.CreateDirectory(nestedDirectory);
        Directory.CreateDirectory(siblingDirectory);
        var credentialPath = WriteText(Path.Combine(credentialsDirectory, "client-secret"));
        var nestedPath = WriteText(Path.Combine(nestedDirectory, "client-secret"));
        var siblingPath = WriteText(Path.Combine(siblingDirectory, "client-secret"));

        ClientCredentialsAccessTokenProvider.IsSystemdManagedCredentialPath(
                credentialPath,
                credentialsDirectory)
            .Should().BeTrue();
        ClientCredentialsAccessTokenProvider.IsSystemdManagedCredentialPath(
                nestedPath,
                credentialsDirectory)
            .Should().BeFalse();
        ClientCredentialsAccessTokenProvider.IsSystemdManagedCredentialPath(
                siblingPath,
                credentialsDirectory)
            .Should().BeFalse();
    }

    [Fact]
    public void IsSystemdManagedCredentialPath_rejects_a_symbolic_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var credentialsDirectory = Path.Combine(_directoryPath, "credentials");
        Directory.CreateDirectory(credentialsDirectory);
        var targetPath = WriteText(Path.Combine(_directoryPath, "target-secret"));
        var credentialPath = Path.Combine(credentialsDirectory, "client-secret");
        File.CreateSymbolicLink(credentialPath, targetPath);

        ClientCredentialsAccessTokenProvider.IsSystemdManagedCredentialPath(
                credentialPath,
                credentialsDirectory)
            .Should().BeFalse();
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

    private static string WriteText(string path)
    {
        File.WriteAllText(path, "sandbox-secret-value");
        return path;
    }
}
