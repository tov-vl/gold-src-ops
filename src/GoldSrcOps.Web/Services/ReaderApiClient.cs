using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Incidents;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using Microsoft.AspNetCore.WebUtilities;

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

    public async Task<IReadOnlyList<AvailabilityIncidentResponse>> GetOpenIncidentsAsync(
        CancellationToken cancellationToken = default) =>
        await GetRequiredAsync<AvailabilityIncidentResponse[]>("api/incidents/open", cancellationToken);

    public async Task<IReadOnlyList<AvailabilityIncidentResponse>> GetServerIncidentsAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var requestUri = AddLimit($"api/servers/{serverId:D}/incidents", limit);
        return await GetRequiredAsync<AvailabilityIncidentResponse[]>(requestUri, cancellationToken);
    }

    public Task<SnapshotHistoryResponse?> GetServerSnapshotsAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var requestUri = AddLimit($"api/servers/{serverId:D}/snapshots", limit);
        return GetOptionalAsync<SnapshotHistoryResponse>(requestUri, cancellationToken);
    }

    private static string AddLimit(string requestUri, int limit) =>
        QueryHelpers.AddQueryString(
            requestUri,
            "limit",
            limit.ToString(CultureInfo.InvariantCulture));

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
