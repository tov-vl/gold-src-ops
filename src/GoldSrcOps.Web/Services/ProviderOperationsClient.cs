using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GoldSrcOps.Contracts.ProviderDelivery;

namespace GoldSrcOps.Web.Services;

internal sealed class ProviderOperationsClient(HttpClient httpClient) : IProviderOperationsClient
{
    private const string NotDeadLetterCode = "provider_delivery.message_not_dead_letter";
    private const string IdempotencyConflictCode = "provider_delivery.idempotency_conflict";
    private const string AlreadyReviewedCode = "provider_delivery.already_reviewed";

    public bool IsEnabled => true;

    public async Task<ProviderDeliveryStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            "internal/v1/provider-delivery/status",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<ProviderDeliveryStatusResponse>(response, cancellationToken);
    }

    public async Task<ProviderDeadLetterListResponse> GetDeadLettersAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        var path = $"internal/v1/provider-delivery/dead-letters?limit={limit}";
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            path += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        using var response = await httpClient.GetAsync(
            path,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredAsync<ProviderDeadLetterListResponse>(response, cancellationToken);
    }

    public async Task<ProviderDeadLetterListItemResponse?> GetDeadLetterAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"internal/v1/provider-delivery/dead-letters/{messageId:D}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var result = await ReadRequiredAsync<ProviderDeadLetterListItemResponse>(
            response,
            cancellationToken);
        return result.MessageId == messageId
            ? result
            : throw new InvalidDataException(
                "The provider operations API returned a mismatched dead-letter message.");
    }

    public async Task<ProviderDeadLetterReviewResult> ReviewDeadLetterAsync(
        Guid messageId,
        Guid requestId,
        string requestedBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/v1/provider-delivery/dead-letters/{messageId:D}/review")
        {
            Content = JsonContent.Create(new ProviderDeadLetterReviewRequest(requestedBy, reason))
        };
        request.Headers.Add("Idempotency-Key", requestId.ToString("D"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK)
        {
            var review = await ReadRequiredAsync<ProviderDeadLetterReviewResponse>(
                response,
                cancellationToken);
            if (review.RequestId != requestId || review.MessageId != messageId)
            {
                throw new InvalidDataException(
                    "The provider operations API returned a mismatched review receipt.");
            }

            return new ProviderDeadLetterReviewResult(
                response.StatusCode == HttpStatusCode.Accepted
                    ? ProviderDeadLetterReviewResultKind.Accepted
                    : ProviderDeadLetterReviewResultKind.Idempotent,
                review);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new ProviderDeadLetterReviewResult(
                await ReadConflictKindAsync(response, cancellationToken));
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new(ProviderDeadLetterReviewResultKind.NotFound),
            HttpStatusCode.BadRequest => new(ProviderDeadLetterReviewResultKind.Rejected),
            _ => throw new HttpRequestException(
                "The provider operations API returned an unexpected review status code.",
                inner: null,
                response.StatusCode)
        };
    }

    public async Task<ProviderDeadLetterReviewResponse?> GetReviewAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"internal/v1/provider-delivery/reviews/{requestId:D}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var review = await ReadRequiredAsync<ProviderDeadLetterReviewResponse>(
            response,
            cancellationToken);
        return review.RequestId == requestId
            ? review
            : throw new InvalidDataException(
                "The provider operations API returned a mismatched review request.");
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        return result ?? throw new InvalidDataException(
            "The provider operations API returned an empty success response.");
    }

    private static async Task<ProviderDeadLetterReviewResultKind> ReadConflictKindAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String)
            {
                return code.GetString() switch
                {
                    NotDeadLetterCode => ProviderDeadLetterReviewResultKind.NotDeadLetter,
                    IdempotencyConflictCode => ProviderDeadLetterReviewResultKind.IdempotencyConflict,
                    AlreadyReviewedCode => ProviderDeadLetterReviewResultKind.AlreadyReviewed,
                    _ => ProviderDeadLetterReviewResultKind.Rejected
                };
            }
        }
        catch (JsonException)
        {
        }

        return ProviderDeadLetterReviewResultKind.Rejected;
    }
}

internal sealed class DisabledProviderOperationsClient : IProviderOperationsClient
{
    public bool IsEnabled => false;

    public Task<ProviderDeliveryStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        Disabled<ProviderDeliveryStatusResponse>();

    public Task<ProviderDeadLetterListResponse> GetDeadLettersAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default) =>
        Disabled<ProviderDeadLetterListResponse>();

    public Task<ProviderDeadLetterListItemResponse?> GetDeadLetterAsync(
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        Disabled<ProviderDeadLetterListItemResponse?>();

    public Task<ProviderDeadLetterReviewResult> ReviewDeadLetterAsync(
        Guid messageId,
        Guid requestId,
        string requestedBy,
        string reason,
        CancellationToken cancellationToken = default) =>
        Disabled<ProviderDeadLetterReviewResult>();

    public Task<ProviderDeadLetterReviewResponse?> GetReviewAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        Disabled<ProviderDeadLetterReviewResponse?>();

    private static Task<T> Disabled<T>() => Task.FromException<T>(
        new InvalidOperationException("Provider operations are disabled."));
}
