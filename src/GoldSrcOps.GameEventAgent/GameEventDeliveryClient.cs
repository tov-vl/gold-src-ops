using System.Net;
using System.Net.Http.Headers;
using GoldSrcOps.Contracts.GameEvents;

namespace GoldSrcOps.GameEventAgent;

internal sealed class GameEventDeliveryClient : IGameEventDeliveryClient
{
    public const string HttpClientName = "game-event-agent-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGameEventAccessTokenProvider _accessTokenProvider;
    private readonly GameEventDeliveryOptions _options;

    public GameEventDeliveryClient(
        IHttpClientFactory httpClientFactory,
        IGameEventAccessTokenProvider accessTokenProvider,
        GameEventDeliveryOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _accessTokenProvider = accessTokenProvider;
        _options = options;
    }

    public async Task<GameEventDeliveryResult> SendAsync(
        QueuedGameEvent gameEvent,
        CancellationToken cancellationToken)
    {
        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider
                .GetAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GameEventTokenException)
        {
            return GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Unauthorized);
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            FormattableString.Invariant($"api/servers/{_options.ServerId:D}/game-events"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(gameEvent.PayloadUtf8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted)
            {
                return await ValidateReceiptAsync(
                    response,
                    gameEvent,
                    cancellationToken).ConfigureAwait(false);
            }

            return ClassifyFailure(response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Network);
        }
        catch (HttpRequestException)
        {
            return GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Network);
        }
        catch (IOException)
        {
            return GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Network);
        }
    }

    private async Task<GameEventDeliveryResult> ValidateReceiptAsync(
        HttpResponseMessage response,
        QueuedGameEvent gameEvent,
        CancellationToken cancellationToken)
    {
        GameEventIngestResponse? receipt;
        try
        {
            receipt = await GameEventJson.ReadBoundedAsync<GameEventIngestResponse>(
                response.Content,
                GameEventJson.MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or System.Text.Json.JsonException)
        {
            return GameEventDeliveryResult.Permanent(GameEventDeliveryFailure.InvalidReceipt);
        }

        var expectedDuplicate = response.StatusCode == HttpStatusCode.OK;
        if (receipt is null ||
            receipt.EventId != gameEvent.EventId ||
            receipt.ServerId != _options.ServerId ||
            receipt.SourceInstanceId != gameEvent.SourceInstanceId ||
            receipt.SequenceNumber != gameEvent.SequenceNumber ||
            receipt.ContractVersion != gameEvent.ContractVersion ||
            !string.Equals(receipt.Type, gameEvent.Type, StringComparison.Ordinal) ||
            receipt.OccurredAtUtc.ToUniversalTime() != gameEvent.OccurredAtUtc ||
            receipt.Duplicate != expectedDuplicate)
        {
            return GameEventDeliveryResult.Permanent(GameEventDeliveryFailure.InvalidReceipt);
        }

        return expectedDuplicate
            ? GameEventDeliveryResult.Idempotent
            : GameEventDeliveryResult.Accepted;
    }

    private GameEventDeliveryResult ClassifyFailure(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.Unauthorized)
        {
            _accessTokenProvider.Invalidate();
            return GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Unauthorized);
        }

        return statusCode switch
        {
            HttpStatusCode.Forbidden =>
                GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Forbidden),
            HttpStatusCode.NotFound =>
                GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.ServerNotFound),
            HttpStatusCode.RequestTimeout =>
                GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Network),
            HttpStatusCode.TooManyRequests =>
                GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Throttled),
            HttpStatusCode.Conflict =>
                GameEventDeliveryResult.Permanent(GameEventDeliveryFailure.Conflict),
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnprocessableEntity =>
                GameEventDeliveryResult.Permanent(GameEventDeliveryFailure.InvalidRequest),
            _ when (int)statusCode >= (int)HttpStatusCode.InternalServerError =>
                GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.RemoteServer),
            _ => GameEventDeliveryResult.Permanent(GameEventDeliveryFailure.Rejected)
        };
    }
}
