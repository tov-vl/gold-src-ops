using System.Text.Json;
using GoldSrcOps.Api.Security;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Contracts.Alerts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace GoldSrcOps.Api.Endpoints;

public static class AlertDeliveryEndpoints
{
    public static RouteGroupBuilder MapAlertDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/alert-delivery")
            .WithTags("Alert Delivery")
            .RequireAuthorization(GoldSrcOpsSecurity.ReaderPolicy);

        group.MapGet("/dead-letters", ListDeadLettersAsync)
            .WithName("ListDeadLetterMessages");

        group.MapGet("/dead-letters/{eventId:guid}", GetDeadLetterAsync)
            .WithName("GetDeadLetterMessage");

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
