using System.Data;
using GoldSrcOps.AlertReceiver.Configuration;
using GoldSrcOps.AlertReceiver.Persistence;
using GoldSrcOps.Application.Alerts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoldSrcOps.AlertReceiver.AvailabilityEvents;

internal sealed partial class AvailabilityEventIngestionService(
    AlertReceiverDbContext dbContext,
    IOptions<AlertReceiverOptions> options,
    TimeProvider timeProvider,
    ILogger<AvailabilityEventIngestionService> logger)
{
    public async Task<AvailabilityEventIngestionResult> IngestAsync(
        AvailabilityEventRequest request,
        CancellationToken cancellationToken)
    {
        var payload = AvailabilityEventPayload.Create(request);
        var receiverMode = options.Value.Mode;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await AcquireLockAsync($"event:{request.EventId:D}", cancellationToken);

        var existingEvent = await dbContext.ReceivedEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.EventId, cancellationToken);
        if (existingEvent is not null)
        {
            await transaction.CommitAsync(cancellationToken);

            if (string.Equals(
                    existingEvent.PayloadSha256,
                    payload.Sha256,
                    StringComparison.Ordinal))
            {
                LogDuplicate(logger, request.EventId, request.IncidentId);
                return AvailabilityEventIngestionResult.Duplicate();
            }

            LogHashConflict(logger, request.EventId);
            return AvailabilityEventIngestionResult.Conflict(
                "Event ID was already used for a different payload.");
        }

        await AcquireLockAsync($"incident:{request.IncidentId:D}", cancellationToken);

        var incident = await dbContext.Incidents
            .SingleOrDefaultAsync(x => x.Id == request.IncidentId, cancellationToken);
        var transitionError = ApplyTransition(request, ref incident);
        if (transitionError is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return AvailabilityEventIngestionResult.Conflict(transitionError);
        }

        var receivedAtUtc = timeProvider.GetUtcNow();
        var receivedEvent = ReceivedAvailabilityEvent.Create(
            request,
            payload,
            receiverMode,
            receivedAtUtc);
        dbContext.ReceivedEvents.Add(receivedEvent);

        if (receiverMode == ReceiverMode.Live)
        {
            dbContext.ProviderOutboxMessages.Add(ProviderOutboxMessage.Create(
                request.EventId,
                request.IncidentId,
                GetProviderAction(request.EventType),
                payload.Json,
                receivedAtUtc));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        LogAccepted(logger, request.EventId, request.IncidentId, receiverMode);
        return AvailabilityEventIngestionResult.Accepted();
    }

    private string? ApplyTransition(
        AvailabilityEventRequest request,
        ref ReceiverIncident? incident)
    {
        if (string.Equals(
                request.EventType,
                IncidentAlertEvents.ServerUnavailable,
                StringComparison.Ordinal))
        {
            if (incident is not null)
            {
                return "Incident already exists; only its matching recovery can follow.";
            }

            incident = ReceiverIncident.Open(request);
            dbContext.Incidents.Add(incident);
            return null;
        }

        if (incident is null)
        {
            return "Recovery event has no recorded unavailable event.";
        }

        return incident.TryResolve(request);
    }

    private static ProviderOutboxAction GetProviderAction(string eventType) =>
        string.Equals(
            eventType,
            IncidentAlertEvents.ServerUnavailable,
            StringComparison.Ordinal)
            ? ProviderOutboxAction.Trigger
            : ProviderOutboxAction.Resolve;

    private Task<int> AcquireLockAsync(string key, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0));",
            cancellationToken);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Availability event {EventId} was already accepted for incident {IncidentId}.")]
    private static partial void LogDuplicate(
        ILogger logger,
        Guid eventId,
        Guid incidentId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Availability event {EventId} was reused with a different payload hash.")]
    private static partial void LogHashConflict(ILogger logger, Guid eventId);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Accepted availability event {EventId} for incident {IncidentId} in {ReceiverMode} mode.")]
    private static partial void LogAccepted(
        ILogger logger,
        Guid eventId,
        Guid incidentId,
        ReceiverMode receiverMode);
}
