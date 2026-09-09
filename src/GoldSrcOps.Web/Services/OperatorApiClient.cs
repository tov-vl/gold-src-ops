using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.Web.Services;

internal sealed class OperatorApiClient(HttpClient httpClient) : IOperatorApiClient
{
    public async Task<OperatorServerRegistrationResult> RegisterServerAsync(
        OperatorServerRegistrationDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/servers")
        {
            Content = JsonContent.Create(new RegisterServerRequest(
                draft.Name,
                draft.Host,
                draft.QueryPort,
                draft.RconPort,
                draft.PollIntervalSeconds,
                draft.Notes,
                IsEnabled: false))
        };
        request.Headers.Add("Idempotency-Key", draft.RequestId.ToString("D"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            var server = await response.Content.ReadFromJsonAsync<ServerResponse>(cancellationToken);
            if (server is null)
            {
                throw new InvalidDataException(
                    "The server registration API returned an empty success response.");
            }

            return new OperatorServerRegistrationResult(
                response.StatusCode == HttpStatusCode.Created
                    ? OperatorServerRegistrationResultKind.Created
                    : OperatorServerRegistrationResultKind.Idempotent,
                server);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Conflict => new OperatorServerRegistrationResult(
                OperatorServerRegistrationResultKind.Conflict,
                Server: null),
            HttpStatusCode.BadRequest => new OperatorServerRegistrationResult(
                OperatorServerRegistrationResultKind.Rejected,
                Server: null),
            _ => throw new HttpRequestException(
                "The server registration API returned an unexpected status code.",
                inner: null,
                response.StatusCode)
        };
    }

    public async Task<OperatorCommandQueueResult> QueueSayAsync(
        Guid serverId,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/servers/{serverId:D}/commands/say")
        {
            Content = JsonContent.Create(new SayCommandRequest(message))
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        return response.StatusCode switch
        {
            HttpStatusCode.Created => OperatorCommandQueueResult.Queued,
            HttpStatusCode.NotFound => OperatorCommandQueueResult.ServerNotFound,
            HttpStatusCode.Conflict => OperatorCommandQueueResult.MissingRconCredential,
            HttpStatusCode.BadRequest => OperatorCommandQueueResult.Rejected,
            _ => throw new HttpRequestException(
                "The command API returned an unexpected status code.",
                inner: null,
                response.StatusCode)
        };
    }

    public async Task<OperatorMonitoringUpdateResult> SetMonitoringEnabledAsync(
        Guid serverId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/servers/{serverId:D}/{(enabled ? "enable" : "disable")}");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => OperatorMonitoringUpdateResult.Updated,
            HttpStatusCode.NotFound => OperatorMonitoringUpdateResult.ServerNotFound,
            _ => throw new HttpRequestException(
                "The server monitoring API returned an unexpected status code.",
                inner: null,
                response.StatusCode)
        };
    }

    public async Task<OperatorReplayResult> ReplayDeadLetterAsync(
        Guid eventId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/alert-delivery/dead-letters/{eventId:D}/replay")
        {
            Content = JsonContent.Create(new ReplayDeadLetterRequest(reason))
        };
        request.Headers.Add("Idempotency-Key", requestId.ToString("D"));

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        return response.StatusCode switch
        {
            HttpStatusCode.Accepted => OperatorReplayResult.Accepted,
            HttpStatusCode.NotFound => OperatorReplayResult.EventNotFound,
            HttpStatusCode.Conflict => OperatorReplayResult.Conflict,
            HttpStatusCode.BadRequest => OperatorReplayResult.Rejected,
            _ => throw new HttpRequestException(
                "The replay API returned an unexpected status code.",
                inner: null,
                response.StatusCode)
        };
    }
}
