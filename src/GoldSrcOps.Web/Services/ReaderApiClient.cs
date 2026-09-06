using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;

namespace GoldSrcOps.Web.Services;

internal sealed class ReaderApiClient(HttpClient httpClient) : IReaderApiClient
{
    public Task<DashboardOverviewResponse> GetOverviewAsync(
        CancellationToken cancellationToken = default) =>
        GetRequiredAsync<DashboardOverviewResponse>("api/dashboard/overview", cancellationToken);

    public async Task<IReadOnlyList<ServerResponse>> GetServersAsync(
        CancellationToken cancellationToken = default) =>
        await GetRequiredAsync<ServerResponse[]>("api/servers/", cancellationToken);

    public Task<ServerResponse?> GetServerAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetOptionalAsync<ServerResponse>($"api/servers/{serverId:D}", cancellationToken);

    public Task<ServerStatusResponse?> GetServerStatusAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        GetOptionalAsync<ServerStatusResponse>($"api/servers/{serverId:D}/status", cancellationToken);

    private async Task<T> GetRequiredAsync<T>(
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException("The API response was empty.");
    }

    private async Task<T?> GetOptionalAsync<T>(
        string requestUri,
        CancellationToken cancellationToken)
        where T : class
    {
        using var response = await httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException("The API response was empty.");
    }
}
