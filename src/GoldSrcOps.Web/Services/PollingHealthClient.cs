using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal sealed class PollingHealthClient(HttpClient client) : IPollingHealthClient
{
    public async Task<PollingHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/operator/polling-health");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType;
        if (!string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Polling health response has an unsupported content type.");
        }

        await response.Content.LoadIntoBufferAsync(16384, timeout.Token).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PollingHealthResponse>(timeout.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Polling health response is unavailable.");
    }
}
