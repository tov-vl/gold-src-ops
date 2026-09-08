using System.Net;
using System.Net.Http.Json;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;

namespace GoldSrcOps.Web.Services;

internal sealed class OperatorApiClient(HttpClient httpClient) : IOperatorApiClient
{
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
