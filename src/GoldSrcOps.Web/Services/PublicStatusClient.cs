using System.Net.Http.Json;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

public sealed class PublicStatusClient(HttpClient httpClient)
{
    public async Task<PublicStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            "api/public/status",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PublicStatusResponse>(cancellationToken)
            ?? throw new InvalidDataException("The public status response was empty.");
    }
}
