using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.Commands;
using GoldSrcOps.Contracts.Credentials;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.Web.Services;

internal sealed class OperatorApiClient(HttpClient httpClient) : IOperatorApiClient
{
    private const string MonitoringMustBePausedCode =
        "rcon_credential.monitoring_must_be_paused";
    private const string CommandsInProgressCode = "rcon_credential.commands_in_progress";

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
            HttpStatusCode.Conflict => OperatorMonitoringUpdateResult.Conflict,
            _ => throw new HttpRequestException(
                "The server monitoring API returned an unexpected status code.",
                inner: null,
                response.StatusCode)
        };
    }

    public async Task<OperatorServerUpdateResult> UpdateServerAsync(
        OperatorServerUpdateDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"api/servers/{draft.ServerId:D}")
        {
            Content = JsonContent.Create(new UpdateServerRequest(
                draft.ExpectedRevision,
                draft.Name,
                draft.Host,
                draft.QueryPort,
                draft.RconPort,
                draft.PollIntervalSeconds,
                draft.Notes))
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var server = await response.Content.ReadFromJsonAsync<ServerResponse>(cancellationToken);
            if (server is null || server.Id != draft.ServerId)
            {
                throw new InvalidDataException(
                    "The server update API returned an invalid success response.");
            }

            return new OperatorServerUpdateResult(
                OperatorServerUpdateResultKind.Updated,
                server);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new OperatorServerUpdateResult(
                OperatorServerUpdateResultKind.ServerNotFound,
                Server: null),
            HttpStatusCode.Conflict => new OperatorServerUpdateResult(
                OperatorServerUpdateResultKind.Conflict,
                Server: null),
            HttpStatusCode.BadRequest => new OperatorServerUpdateResult(
                OperatorServerUpdateResultKind.Rejected,
                Server: null),
            _ => throw new HttpRequestException(
                "The server update API returned an unexpected status code.",
                inner: null,
                response.StatusCode)
        };
    }

    public async Task<OperatorRconCredentialUpdateResult> SetRconCredentialAsync(
        OperatorRconCredentialDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"api/servers/{draft.ServerId:D}/credentials/rcon")
        {
            Content = JsonContent.Create(new SetRconCredentialRequest(
                draft.ExpectedServerRevision,
                draft.ExpectedCredentialRevision,
                draft.SecretAlias))
        };
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var credential = await response.Content.ReadFromJsonAsync<ServerCredentialResponse>(
                cancellationToken);
            if (credential is null ||
                credential.ServerId != draft.ServerId ||
                credential.Revision <= 0 ||
                !credential.IsConfigured ||
                !string.Equals(credential.Kind, "RconPassword", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The credential API returned an invalid success response.");
            }

            return new OperatorRconCredentialUpdateResult(
                OperatorRconCredentialUpdateResultKind.Updated,
                credential);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new OperatorRconCredentialUpdateResult(
                await MapCredentialConflictAsync(response, cancellationToken),
                Credential: null);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new OperatorRconCredentialUpdateResult(
                OperatorRconCredentialUpdateResultKind.ServerNotFound,
                Credential: null),
            HttpStatusCode.BadRequest => new OperatorRconCredentialUpdateResult(
                OperatorRconCredentialUpdateResultKind.Rejected,
                Credential: null),
            _ => throw new HttpRequestException(
                "The credential API returned an unexpected status code.",
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

    private static async Task<OperatorRconCredentialUpdateResultKind> MapCredentialConflictAsync(
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
                    MonitoringMustBePausedCode =>
                        OperatorRconCredentialUpdateResultKind.MonitoringEnabled,
                    CommandsInProgressCode =>
                        OperatorRconCredentialUpdateResultKind.CommandsInProgress,
                    _ => OperatorRconCredentialUpdateResultKind.Conflict
                };
            }
        }
        catch (JsonException)
        {
        }

        return OperatorRconCredentialUpdateResultKind.Conflict;
    }
}
