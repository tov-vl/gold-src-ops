using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

internal sealed class ArchiveHealthClient(HttpClient client) : IArchiveHealthClient
{
    public async Task<ArchiveHealthResponse> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/operator/archive-health");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType;
        if (!string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            contentType?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Archive health response has an unsupported content type.");
        }

        await response.Content.LoadIntoBufferAsync(16384, timeout.Token).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ArchiveHealthResponse>(timeout.Token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Archive health response is unavailable.");
    }
}
