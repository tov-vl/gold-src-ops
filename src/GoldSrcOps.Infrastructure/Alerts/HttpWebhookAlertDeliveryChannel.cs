using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GoldSrcOps.Application.Alerts;

namespace GoldSrcOps.Infrastructure.Alerts;

internal sealed class HttpWebhookAlertDeliveryChannel : IAlertDeliveryChannel, IDisposable
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const int MaxResponseHeadersLengthKilobytes = 16;

    private readonly HttpWebhookDeliverySettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _httpClient;

    public HttpWebhookAlertDeliveryChannel(HttpWebhookDeliverySettings settings)
        : this(settings, TimeProvider.System)
    {
    }

    internal HttpWebhookAlertDeliveryChannel(
        HttpWebhookDeliverySettings settings,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _settings = settings;
        _timeProvider = timeProvider;
        _httpClient = new HttpClient(CreateHandler(settings.RequestTimeout), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<AlertDeliveryAttemptResult> DeliverAsync(
        ClaimedOutboxMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        using var request = CreateRequest(message);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_settings.RequestTimeout);

        try
        {
            // The delivery contract uses only status and Retry-After, so untrusted bodies are never buffered or read.
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token).ConfigureAwait(false);

            return Classify(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AlertDeliveryAttemptResult.RetryableFailure(AlertDeliveryFailureCategory.Timeout);
        }
        catch (HttpRequestException)
        {
            return AlertDeliveryAttemptResult.RetryableFailure(AlertDeliveryFailureCategory.Transport);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static SocketsHttpHandler CreateHandler(TimeSpan requestTimeout) =>
        new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = requestTimeout,
            MaxResponseHeadersLength = MaxResponseHeadersLengthKilobytes,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };

    private HttpRequestMessage CreateRequest(ClaimedOutboxMessage message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint)
        {
            Content = new StringContent(message.Payload, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(
            IdempotencyKeyHeaderName,
            message.Id.ToString("D", CultureInfo.InvariantCulture));
        request.Headers.Authorization = _settings.Authorization;

        return request;
    }

    private AlertDeliveryAttemptResult Classify(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            return AlertDeliveryAttemptResult.Delivered();
        }

        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            statusCode is >= 500 and <= 599)
        {
            return AlertDeliveryAttemptResult.RetryableFailure(
                AlertDeliveryFailureCategory.RemoteResponse,
                statusCode,
                GetBoundedRetryAfter(response.Headers.RetryAfter));
        }

        return AlertDeliveryAttemptResult.PermanentFailure(
            AlertDeliveryFailureCategory.RemoteResponse,
            statusCode);
    }

    private TimeSpan? GetBoundedRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null)
        {
            return null;
        }

        var delay = retryAfter.Delta;
        if (delay is null && retryAfter.Date is { } retryAtUtc)
        {
            delay = retryAtUtc - _timeProvider.GetUtcNow();
        }

        return delay is { } value && value >= TimeSpan.Zero && value <= _settings.MaximumRetryAfter
            ? value
            : null;
    }
}
