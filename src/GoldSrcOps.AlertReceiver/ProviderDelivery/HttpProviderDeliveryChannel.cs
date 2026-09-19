using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GoldSrcOps.AlertReceiver.Configuration;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.AlertReceiver.ProviderDelivery;

internal sealed class HttpProviderDeliveryChannel : IProviderDeliveryChannel, IDisposable
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private readonly ProviderDeliveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly HttpClient _client;

    public HttpProviderDeliveryChannel(
        IOptions<ProviderDeliveryOptions> options,
        TimeProvider timeProvider)
        : this(options.Value, timeProvider, CreateClient(options.Value))
    {
    }

    internal HttpProviderDeliveryChannel(
        ProviderDeliveryOptions options,
        TimeProvider timeProvider,
        HttpClient client)
    {
        _options = options;
        _timeProvider = timeProvider;
        _client = client;
    }

    public async Task<ProviderDeliveryResult> DeliverAsync(
        ClaimedProviderOutboxMessage message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ProviderDeliveryEnvelope(
            Version: 1,
            message.Action,
            GroupKey: $"goldsrcops-availability:{message.IncidentId:D}",
            message.SourceEventId,
            message.IncidentId,
            message.Payload), JsonSerializerOptions.Web);
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = payload.Length;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = content,
        };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(_options.Authorization);
        request.Headers.Add(
            IdempotencyKeyHeader,
            message.SourceEventId.ToString("D", CultureInfo.InvariantCulture));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var statusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return ProviderDeliveryResult.Delivered();
            }

            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                statusCode is >= 500 and <= 599)
            {
                return ProviderDeliveryResult.Retryable(
                    $"Provider returned HTTP status {statusCode}.",
                    GetRetryAfter(response.Headers.RetryAfter));
            }

            return ProviderDeliveryResult.Permanent(
                $"Provider returned HTTP status {statusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderDeliveryResult.Retryable("Provider request timed out.");
        }
        catch (HttpRequestException)
        {
            return ProviderDeliveryResult.Retryable("Provider transport failed.");
        }
    }

    public void Dispose() => _client.Dispose();

    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } retryAtUtc)
        {
            delay = retryAtUtc - _timeProvider.GetUtcNow();
        }

        return delay is { } value && value >= TimeSpan.Zero && value <= _options.MaximumRetryAfter
            ? value
            : null;
    }

    private static HttpClient CreateClient(ProviderDeliveryOptions options) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.RequestTimeout,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private sealed record ProviderDeliveryEnvelope(
        int Version,
        string Action,
        string GroupKey,
        Guid SourceEventId,
        Guid IncidentId,
        System.Text.Json.JsonElement Payload);
}
