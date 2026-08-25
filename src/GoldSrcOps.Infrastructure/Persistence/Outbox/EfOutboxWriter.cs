using System.Text.Json;
using GoldSrcOps.Application.Alerts;

namespace GoldSrcOps.Infrastructure.Persistence.Outbox;

internal sealed class EfOutboxWriter(GoldSrcOpsDbContext dbContext) : IOutboxWriter
{
    public void Add(IncidentAlertEventV1 alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var message = new OutboxMessage(
            alert.EventId,
            alert.EventType,
            alert.PayloadVersion,
            IncidentAlertEvents.AggregateType,
            alert.IncidentId,
            alert.OccurredAtUtc,
            JsonSerializer.Serialize(alert, JsonSerializerOptions.Web));

        dbContext.OutboxMessages.Add(message);
    }
}
