using System.Text.Json;
using GoldSrcOps.Application.Alerts;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class IncidentAlertEventV1Tests
{
    [Fact]
    public void Contract_has_stable_names_version_and_flat_json_shape()
    {
        var eventId = Guid.NewGuid();
        var incidentId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var openedAtUtc = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var closedAtUtc = openedAtUtc.AddMinutes(3);
        var alert = new IncidentAlertEventV1(
            eventId,
            IncidentAlertEvents.ServerRecovered,
            closedAtUtc,
            incidentId,
            serverId,
            "Public CS 1.6",
            "Server query recovered.",
            3,
            openedAtUtc,
            closedAtUtc,
            180);

        var json = JsonSerializer.Serialize(alert, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(12, root.EnumerateObject().Count());
        Assert.Equal("availability-incident", IncidentAlertEvents.AggregateType);
        Assert.Equal(
            "server.availability.unavailable",
            IncidentAlertEvents.ServerUnavailable);
        Assert.Equal(
            "server.availability.recovered",
            IncidentAlertEvents.ServerRecovered);
        Assert.Equal(eventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal(IncidentAlertEvents.ServerRecovered, root.GetProperty("eventType").GetString());
        Assert.Equal(IncidentAlertEventV1.CurrentPayloadVersion, root.GetProperty("payloadVersion").GetInt16());
        Assert.Equal(closedAtUtc, root.GetProperty("occurredAtUtc").GetDateTimeOffset());
        Assert.Equal(incidentId, root.GetProperty("incidentId").GetGuid());
        Assert.Equal(serverId, root.GetProperty("serverId").GetGuid());
        Assert.Equal("Public CS 1.6", root.GetProperty("serverName").GetString());
        Assert.Equal("Server query recovered.", root.GetProperty("reason").GetString());
        Assert.Equal(3, root.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(openedAtUtc, root.GetProperty("openedAtUtc").GetDateTimeOffset());
        Assert.Equal(closedAtUtc, root.GetProperty("closedAtUtc").GetDateTimeOffset());
        Assert.Equal(180L, root.GetProperty("durationSeconds").GetInt64());
    }
}
