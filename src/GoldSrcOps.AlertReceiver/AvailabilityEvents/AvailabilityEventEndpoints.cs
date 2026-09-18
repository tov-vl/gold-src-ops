using System.Diagnostics;
using GoldSrcOps.AlertReceiver.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace GoldSrcOps.AlertReceiver.AvailabilityEvents;

internal static class AvailabilityEventEndpoints
{
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";
    private const long MaxRequestBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapAvailabilityEventEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/availability-events", IngestAsync)
            .WithName("IngestAvailabilityEvent")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Accepts<AvailabilityEventRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> IngestAsync(
        HttpRequest httpRequest,
        AvailabilityEventRequest request,
        ReceiverAuthorization authorization,
        AvailabilityEventIngestionService ingestionService,
        CancellationToken cancellationToken)
    {
        if (!authorization.IsAuthorized(httpRequest.Headers.Authorization))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Receiver authorization failed.");
        }

        var idempotencyValues = httpRequest.Headers[IdempotencyKeyHeaderName];
        if (idempotencyValues.Count != 1 ||
            !Guid.TryParseExact(idempotencyValues[0], "D", out var idempotencyKey))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "A single canonical Idempotency-Key header is required.");
        }

        if (idempotencyKey != request.EventId)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency-Key must match eventId.");
        }

        var validationErrors = AvailabilityEventRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var result = await ingestionService.IngestAsync(request, cancellationToken);
        return result.Disposition switch
        {
            AvailabilityEventIngestionDisposition.Accepted => Results.Accepted(),
            AvailabilityEventIngestionDisposition.Duplicate => Results.NoContent(),
            AvailabilityEventIngestionDisposition.Conflict => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: result.Error),
            _ => throw new UnreachableException(),
        };
    }
}
