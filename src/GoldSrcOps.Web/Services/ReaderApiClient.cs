using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
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

    public Task<FleetOverviewResponse> GetFleetOverviewAsync(
        CancellationToken cancellationToken = default) =>
        GetRequiredAsync<FleetOverviewResponse>("api/dashboard/fleet", cancellationToken);

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

    public async Task<IReadOnlyList<CommandExecutionResponse>?> GetServerCommandsAsync(
        Guid serverId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var requestUri = AddLimit($"api/servers/{serverId:D}/commands", limit);
        return await GetOptionalAsync<CommandExecutionResponse[]>(requestUri, cancellationToken);
    }

    public async Task<IReadOnlyList<ServerCredentialResponse>?> GetServerCredentialsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        await GetOptionalAsync<ServerCredentialResponse[]>(
            $"api/servers/{serverId:D}/credentials",
            cancellationToken);

    public Task<DeadLetterListResponse> GetDeadLettersAsync(
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var requestUri = AddLimit("api/alert-delivery/dead-letters", limit);
        if (cursor is not null)
        {
            requestUri = QueryHelpers.AddQueryString(requestUri, "cursor", cursor);
        }

        return GetRequiredAsync<DeadLetterListResponse>(requestUri, cancellationToken);
    }

    public Task<DeadLetterDetailResponse?> GetDeadLetterAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        GetOptionalAsync<DeadLetterDetailResponse>(
            $"api/alert-delivery/dead-letters/{eventId:D}",
            cancellationToken);

    public Task<DeadLetterReplayResponse?> GetDeadLetterReplayAsync(
        Guid requestId,
        CancellationToken cancellationToken = default) =>
        GetOptionalAsync<DeadLetterReplayResponse>(
            $"api/alert-delivery/replays/{requestId:D}",
            cancellationToken);

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
