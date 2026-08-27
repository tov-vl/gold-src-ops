using System.Security.Claims;
using System.Text.Json;
using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Contracts.Alerts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace GoldSrcOps.Api.Endpoints;

public static class AlertDeliveryEndpoints
{
    private const string InvalidReplayCode = "alert_delivery.replay_invalid";
    private const string EventNotFoundCode = "alert_delivery.event_not_found";
    private const string EventNotDeadLetterCode = "alert_delivery.event_not_dead_letter";
    private const string NewerEventProcessingCode = "alert_delivery.newer_event_processing";
    private const string IdempotencyConflictCode = "alert_delivery.idempotency_conflict";
    private const string EventNotReplayableCode = "alert_delivery.event_not_replayable";
    private const string ReplayNotFoundCode = "alert_delivery.replay_not_found";

    public static RouteGroupBuilder MapAlertDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/alert-delivery")
            .WithTags("Alert Delivery")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/dead-letters", ListDeadLettersAsync)
            .WithName("ListDeadLetterMessages");

        group.MapGet("/dead-letters/{eventId:guid}", GetDeadLetterAsync)
            .WithName("GetDeadLetterMessage");

        group.MapPost("/dead-letters/{eventId:guid}/replay", ReplayDeadLetterAsync)
            .WithName("ReplayDeadLetterMessage")
            .RequireAuthorization(GoldSrcOpsSecurity.OperatorPolicy);

        group.MapGet("/replays/{requestId:guid}", GetReplayAsync)
            .WithName("GetDeadLetterReplay");

        return group;
    }

    private static async Task<Results<Ok<DeadLetterListResponse>, ValidationProblem>> ListDeadLettersAsync(
        string? cursor,
        int? limit,
        AlertDeliveryReadService alertDelivery,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        DeadLetterPagePosition? position = null;

        if (cursor is not null && !DeadLetterCursor.TryDecode(cursor, out position))
        {
            errors.Add("cursor", ["Cursor is invalid or no longer supported."]);
        }

        if (limit is < 1 or > AlertDeliveryReadService.MaxDeadLetterLimit)
        {
            errors.Add(
                "limit",
                [$"Limit must be between 1 and {AlertDeliveryReadService.MaxDeadLetterLimit}."]);
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var page = await alertDelivery.ListDeadLettersAsync(position, limit, cancellationToken);
        return TypedResults.Ok(Map(page));
    }

    private static async Task<Results<Ok<DeadLetterDetailResponse>, NotFound>> GetDeadLetterAsync(
        Guid eventId,
        AlertDeliveryReadService alertDelivery,
        CancellationToken cancellationToken)
    {
        var details = await alertDelivery.GetDeadLetterAsync(eventId, cancellationToken);
        return details is null ? TypedResults.NotFound() : TypedResults.Ok(Map(details));
    }

    private static async Task<Results<Accepted<DeadLetterReplayResponse>, ValidationProblem, ProblemHttpResult>>
        ReplayDeadLetterAsync(
            Guid eventId,
            ReplayDeadLetterRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            ClaimsPrincipal principal,
            AlertDeliveryReplayService alertDelivery,
            CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (!Guid.TryParseExact(idempotencyKey, "D", out var requestId) ||
            requestId == Guid.Empty)
        {
            errors.Add(
                "Idempotency-Key",
                ["Idempotency-Key must be a non-empty UUID in canonical form."]);
        }

        if (!AlertDeliveryReplayService.TryNormalizeReason(
                request.Reason,
                out var normalizedReason))
        {
            errors.Add(
                "reason",
                [$"Reason must contain between 1 and {AlertDeliveryReplayService.MaxReasonLength} characters after trimming."]);
        }

        if (errors.Count > 0)
        {
            GoldSrcOpsMetrics.RecordAlertReplayRequest(AlertReplayMetricResult.Invalid);
            return TypedResults.ValidationProblem(
                errors,
                extensions: ProblemCode(InvalidReplayCode));
        }

        var result = await alertDelivery.ReplayAsync(
            new DeadLetterReplayCommand(
                requestId,
                eventId,
                GoldSrcOpsSecurity.GetRequiredSubject(principal),
                normalizedReason),
            cancellationToken);

        return MapReplayResult(result);
    }

    private static async Task<Results<Ok<DeadLetterReplayResponse>, ProblemHttpResult>> GetReplayAsync(
        Guid requestId,
        AlertDeliveryReplayService alertDelivery,
        CancellationToken cancellationToken)
    {
        var replay = await alertDelivery.GetReplayAsync(requestId, cancellationToken);
        return replay is null
            ? ReplayProblem(
                StatusCodes.Status404NotFound,
                "Replay request was not found.",
                ReplayNotFoundCode)
            : TypedResults.Ok(Map(replay));
    }

    private static Results<Accepted<DeadLetterReplayResponse>, ValidationProblem, ProblemHttpResult>
        MapReplayResult(DeadLetterReplayResult result)
    {
        if (result.Kind is DeadLetterReplayResultKind.Accepted or
            DeadLetterReplayResultKind.Idempotent)
        {
            var replay = result.Replay ?? throw new InvalidOperationException(
                "A successful replay result must contain its durable record.");

            return TypedResults.Accepted(
                $"/api/alert-delivery/replays/{replay.RequestId:D}",
                Map(replay));
        }

        return result.Kind switch
        {
            DeadLetterReplayResultKind.EventNotFound => ReplayProblem(
                StatusCodes.Status404NotFound,
                "The alert event was not found.",
                EventNotFoundCode),
            DeadLetterReplayResultKind.EventNotDeadLetter => ReplayProblem(
                StatusCodes.Status409Conflict,
                "The alert event is not currently dead-lettered.",
                EventNotDeadLetterCode),
            DeadLetterReplayResultKind.NewerEventProcessing => ReplayProblem(
                StatusCodes.Status409Conflict,
                "A newer event for the same aggregate is currently processing.",
                NewerEventProcessingCode),
            DeadLetterReplayResultKind.IdempotencyConflict => ReplayProblem(
                StatusCodes.Status409Conflict,
                "The idempotency key was already used for a different replay request.",
                IdempotencyConflictCode),
            DeadLetterReplayResultKind.EventNotReplayable => ReplayProblem(
                StatusCodes.Status409Conflict,
                "The alert event cannot be replayed safely.",
                EventNotReplayableCode),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Kind, null)
        };
    }

    private static ProblemHttpResult ReplayProblem(
        int statusCode,
        string title,
        string code) =>
        TypedResults.Problem(
            statusCode: statusCode,
            title: title,
            extensions: ProblemCode(code));

    private static KeyValuePair<string, object?>[] ProblemCode(string code) =>
        [new("code", code)];

    private static DeadLetterListResponse Map(DeadLetterPageDto page) =>
        new(
            page.Limit,
            page.NextPosition is null ? null : DeadLetterCursor.Encode(page.NextPosition),
            page.Items.Select(Map).ToArray());

    private static DeadLetterListItemResponse Map(DeadLetterListItemDto item) =>
        new(
            item.EventId,
            item.EventType,
            item.PayloadVersion,
            item.AggregateType,
            item.AggregateId,
            item.OccurredAtUtc,
            item.AttemptCount,
            item.ReplayCount,
            item.DeadLetteredAtUtc,
            item.LastError);

    private static DeadLetterReplayResponse Map(DeadLetterReplayRecordDto replay) =>
        new(
            replay.RequestId,
            replay.EventId,
            replay.RequestedBy,
            replay.RequestedAtUtc,
            replay.Reason,
            replay.ReplayNumber,
            replay.PreviousAttemptCount,
            replay.PreviousDeadLetteredAtUtc,
            "Pending",
            replay.NextAttemptAtUtc);

    private static DeadLetterDetailResponse Map(DeadLetterDetailsDto details)
    {
        using var payload = JsonDocument.Parse(details.Payload);

        return new DeadLetterDetailResponse(
            details.EventId,
            details.EventType,
            details.PayloadVersion,
            details.AggregateType,
            details.AggregateId,
            details.OccurredAtUtc,
            payload.RootElement.Clone(),
            details.AttemptCount,
            details.ReplayCount,
            details.DeadLetteredAtUtc,
            details.LastError,
            details.NewerEvent is not null,
            details.NewerEvent?.EventId,
            details.NewerEvent?.Status,
            details.NewerEvent?.OccurredAtUtc);
    }
}
