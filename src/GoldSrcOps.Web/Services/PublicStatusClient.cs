using System.Net.Http.Json;
using GoldSrcOps.Contracts.Monitoring;

namespace GoldSrcOps.Web.Services;

public sealed class PublicStatusClient(HttpClient httpClient)
{
    public const string Last24HoursWindow = "24h";
    public const string Last7DaysWindow = "7d";

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

    public async Task<PublicA2sHistoryResponse> GetA2sHistoryAsync(
        string window,
        CancellationToken cancellationToken = default)
    {
        if (window is not Last24HoursWindow and not Last7DaysWindow)
        {
            throw new ArgumentException("Window must be either '24h' or '7d'.", nameof(window));
        }

        using var response = await httpClient.GetAsync(
            $"api/public/a2s-history?window={window}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PublicA2sHistoryResponse>(cancellationToken)
            ?? throw new InvalidDataException("The public A2S history response was empty.");
    }
}
