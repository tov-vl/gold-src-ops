using System.Net;
using System.Text.Json.Serialization;

namespace GoldSrcOps.GameEventAgent;

internal sealed class ClientCredentialsAccessTokenProvider : IGameEventAccessTokenProvider, IDisposable
{
    public const string HttpClientName = "game-event-agent-oauth";

    private const int MaximumSecretBytes = 4 * 1024;
    private const int MaximumTokenResponseBytes = 32 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OAuthClientCredentialsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _cacheLock = new();
    private string? _accessToken;
    private DateTimeOffset _expiresAtUtc;

    public ClientCredentialsAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        OAuthClientCredentialsOptions options,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetCachedToken(out var cachedToken))
        {
            return cachedToken;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCachedToken(out cachedToken))
            {
                return cachedToken;
            }

            var refreshed = await RequestTokenAsync(cancellationToken).ConfigureAwait(false);
            lock (_cacheLock)
            {
                _accessToken = refreshed.AccessToken;
                _expiresAtUtc = refreshed.ExpiresAtUtc;
            }

            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _accessToken = null;
            _expiresAtUtc = default;
        }
    }

    public void Dispose() => _refreshGate.Dispose();

    private bool TryGetCachedToken(out string token)
    {
        lock (_cacheLock)
        {
            if (_accessToken is not null && _timeProvider.GetUtcNow() < _expiresAtUtc)
            {
                token = _accessToken;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    private async Task<CachedToken> RequestTokenAsync(CancellationToken cancellationToken)
    {
        string clientSecret;
        try
        {
            var secretInfo = new FileInfo(_options.ClientSecretFile);
            if (!secretInfo.Exists || secretInfo.Length is <= 0 or > MaximumSecretBytes)
            {
                throw new GameEventTokenException(
                    "The OAuth client-secret file is missing, empty, or exceeds the size limit.");
            }

            ValidateSecretFilePermissions(secretInfo.FullName);

            clientSecret = (await File.ReadAllTextAsync(
                _options.ClientSecretFile,
                cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(clientSecret))
            {
                throw new GameEventTokenException("The OAuth client-secret file is empty.");
            }
        }
        catch (GameEventTokenException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameEventTokenException("The OAuth client-secret file could not be read.", exception);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = CreateTokenRequestContent(clientSecret)
        };

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new GameEventTokenException(
                    FormattableString.Invariant(
                        $"The OAuth token endpoint returned HTTP {(int)response.StatusCode}."));
            }

            OAuthTokenResponse? tokenResponse;
            try
            {
                tokenResponse = await GameEventJson.ReadBoundedAsync<OAuthTokenResponse>(
                    response.Content,
                    MaximumTokenResponseBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or System.Text.Json.JsonException)
            {
                throw new GameEventTokenException(
                    "The OAuth token endpoint returned an invalid response.",
                    exception);
            }

            if (tokenResponse is null ||
                string.IsNullOrWhiteSpace(tokenResponse.AccessToken) ||
                tokenResponse.AccessToken.Length > 16 * 1024 ||
                !string.Equals(tokenResponse.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                tokenResponse.ExpiresIn <= 0)
            {
                throw new GameEventTokenException(
                    "The OAuth token endpoint returned an invalid token contract.");
            }

            var lifetime = TimeSpan.FromSeconds(tokenResponse.ExpiresIn);
            var cacheLifetime = lifetime > _options.RefreshSkew
                ? lifetime - _options.RefreshSkew
                : TimeSpan.FromTicks(Math.Max(1, lifetime.Ticks / 2));
            return new CachedToken(
                tokenResponse.AccessToken,
                _timeProvider.GetUtcNow() + cacheLifetime);
        }
        catch (GameEventTokenException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GameEventTokenException("The OAuth token request timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new GameEventTokenException("The OAuth token endpoint is unavailable.", exception);
        }
    }

    private FormUrlEncodedContent CreateTokenRequestContent(string clientSecret)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", _options.ClientId),
            new("client_secret", clientSecret),
            new("audience", _options.Audience)
        };
        if (!string.IsNullOrWhiteSpace(_options.Scope))
        {
            values.Add(new KeyValuePair<string, string>("scope", _options.Scope));
        }

        return new FormUrlEncodedContent(values);
    }

    private static void ValidateSecretFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode prohibited =
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute;
        if ((mode & UnixFileMode.UserRead) == UnixFileMode.None ||
            (mode & prohibited) != UnixFileMode.None)
        {
            throw new GameEventTokenException(
                "The OAuth client-secret file must be readable by its owner and inaccessible to group and other users.");
        }
    }

    private sealed record OAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);
}
