using System.Security.Claims;
using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Contracts.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace GoldSrcOps.Api.Endpoints;

public static class GameEventEndpoints
{
    private const long MaxRequestBodyBytes = 4 * 1024;
    private const string EventIdConflictCode = "game_event.event_id_conflict";
    private const string SourceSequenceConflictCode = "game_event.source_sequence_conflict";

    public static IEndpointRouteBuilder MapGameEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/servers/{serverId:guid}/game-events", ListAsync)
            .WithTags("Game Events")
            .WithName("ListRecentGameEvents")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        endpoints.MapPost("/api/servers/{serverId:guid}/game-events", IngestAsync)
            .WithTags("Game Events")
            .WithName("IngestGameEvent")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .RequireAuthorization(GoldSrcOpsSecurity.GameEventWriterPolicy);

        return endpoints;
    }

    private static async Task<Results<
        Ok<GameEventHistoryResponse>,
        NotFound,
        ValidationProblem>> ListAsync(
        Guid serverId,
        int? limit,
        GameEventReadService gameEvents,
        CancellationToken cancellationToken)
    {
        if (limit is <= 0 or > GameEventReadService.MaxLimit)
        {
            return TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["limit"] = [$"Limit must be between 1 and {GameEventReadService.MaxLimit}."]
                });
        }

        var result = await gameEvents.ListRecentRoundsAsync(serverId, limit, cancellationToken);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(Map(result));
    }

    private static async Task<Results<
        Accepted<GameEventIngestResponse>,
        Ok<GameEventIngestResponse>,
        NotFound,
        ValidationProblem,
        ForbidHttpResult,
        ProblemHttpResult>> IngestAsync(
        Guid serverId,
        GameEventIngestRequest request,
        HttpRequest httpRequest,
        ClaimsPrincipal principal,
        GameEventIngestionService ingestion,
        CancellationToken cancellationToken)
    {
        if (httpRequest.ContentLength is > MaxRequestBodyBytes)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: $"The request body must not exceed {MaxRequestBodyBytes} bytes.");
        }

        if (!GoldSrcOpsSecurity.TryGetBoundServerId(principal, out var boundServerId) ||
            boundServerId != serverId)
        {
            return TypedResults.Forbid();
        }

        var errors = Validate(request, out var eventType);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await ingestion.IngestAsync(
            serverId,
            new IngestGameEventCommand(
                request.ContractVersion,
                request.EventId,
                request.SourceInstanceId,
                request.SequenceNumber,
                eventType,
                request.OccurredAtUtc,
                request.Map,
                request.Players,
                request.Bots),
            cancellationToken);

        return result.Kind switch
        {
            GameEventIngestResultKind.Accepted => TypedResults.Accepted(
                uri: (string?)null,
                value: Map(result.Event!)),
            GameEventIngestResultKind.Idempotent => TypedResults.Ok(Map(result.Event!)),
            GameEventIngestResultKind.ServerNotFound => TypedResults.NotFound(),
            GameEventIngestResultKind.EventIdConflict => Conflict(
                "The event id was already used for different event content.",
                EventIdConflictCode),
            GameEventIngestResultKind.SourceSequenceConflict => Conflict(
                "The source sequence number was already used by this source instance.",
                SourceSequenceConflictCode),
            GameEventIngestResultKind.OccurredAtInFuture => TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(request.OccurredAtUtc)] =
                    [
                        $"OccurredAtUtc must not be more than {GameEventIngestionService.MaximumFutureClockSkew.TotalMinutes:0} minutes in the future."
                    ]
                }),
            _ => throw new InvalidOperationException(
                $"Unsupported game event ingestion result '{result.Kind}'.")
        };
    }

    private static Dictionary<string, string[]> Validate(
        GameEventIngestRequest request,
        out GameEventType eventType)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.ContractVersion != GameEventInboxEntry.CurrentContractVersion)
        {
            errors[nameof(request.ContractVersion)] =
                [$"ContractVersion must be {GameEventInboxEntry.CurrentContractVersion}."];
        }

        if (request.EventId == Guid.Empty)
        {
            errors[nameof(request.EventId)] = ["EventId must not be empty."];
        }

        if (request.SourceInstanceId == Guid.Empty)
        {
            errors[nameof(request.SourceInstanceId)] = ["SourceInstanceId must not be empty."];
        }

        if (request.SequenceNumber <= 0)
        {
            errors[nameof(request.SequenceNumber)] = ["SequenceNumber must be positive."];
        }

        if (!GameEventTypeContract.TryParse(request.Type, out eventType))
        {
            errors[nameof(request.Type)] =
                ["Type must be one of 'server.started', 'server.stopped', 'map.started', 'round.started', or 'round.ended'."];
        }

        if (request.OccurredAtUtc == default)
        {
            errors[nameof(request.OccurredAtUtc)] = ["OccurredAtUtc is required."];
        }

        var normalizedMap = request.Map?.Trim();
        if (!string.IsNullOrEmpty(normalizedMap) &&
            !GameEventInboxEntry.IsValidMapName(normalizedMap))
        {
            errors[nameof(request.Map)] =
                [$"Map must be at most {GameEventInboxEntry.MaxMapLength} characters and use ASCII letters, digits, underscores, or hyphens."];
        }
        else if (!errors.ContainsKey(nameof(request.Type)) &&
            GameEventInboxEntry.RequiresMap(eventType) &&
            string.IsNullOrEmpty(normalizedMap))
        {
            errors[nameof(request.Map)] = ["Map is required for map and round events."];
        }

        if ((request.Players is null) != (request.Bots is null))
        {
            errors[nameof(request.Players)] =
                ["Players and Bots must either both be supplied or both be omitted."];
        }
        else if (request.Players is int playerCount && request.Bots is int botCount)
        {
            if (playerCount is < 0 or > GameEventInboxEntry.MaxPlayerCount)
            {
                errors[nameof(request.Players)] =
                    [$"Players must be between 0 and {GameEventInboxEntry.MaxPlayerCount}."];
            }

            if (botCount < 0 || botCount > playerCount)
            {
                errors[nameof(request.Bots)] =
                    ["Bots must be non-negative and no greater than Players."];
            }
        }

        return errors;
    }

    private static GameEventIngestResponse Map(GameEventIngestDto gameEvent) =>
        new(
            gameEvent.EventId,
            gameEvent.ServerId,
            gameEvent.SourceInstanceId,
            gameEvent.SequenceNumber,
            gameEvent.ContractVersion,
            GameEventTypeContract.ToWireValue(gameEvent.Type),
            gameEvent.OccurredAtUtc,
            gameEvent.ReceivedAtUtc,
            gameEvent.Duplicate);

    private static GameEventHistoryResponse Map(GameEventHistoryDto history) =>
        new(
            history.ServerId,
            history.Limit,
            history.Items
                .Select(static item => new GameEventHistoryItemResponse(
                    GameEventTypeContract.ToWireValue(item.Type),
                    item.OccurredAtUtc,
                    item.Map,
                    item.Players,
                    item.Bots))
                .ToArray());

    private static ProblemHttpResult Conflict(string title, string code) =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: title,
            extensions:
            [
                new KeyValuePair<string, object?>("code", code)
            ]);
}
