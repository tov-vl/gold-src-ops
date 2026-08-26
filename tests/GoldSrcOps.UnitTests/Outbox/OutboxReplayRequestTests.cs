using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Infrastructure.Persistence.Outbox;

namespace GoldSrcOps.UnitTests.Outbox;

public sealed class OutboxReplayRequestTests
{
    [Fact]
    public void Constructor_captures_immutable_event_metadata_and_normalizes_operator_input()
    {
        var message = CreateMessage();
        var requestId = Guid.NewGuid();
        var requestedAtUtc = new DateTimeOffset(2026, 8, 26, 16, 0, 0, TimeSpan.Zero);

        var request = new OutboxReplayRequest(
            requestId,
            message,
            " operator@example.test ",
            requestedAtUtc,
            " downstream endpoint was corrected ",
            requestedAtUtc);

        Assert.Equal(requestId, request.Id);
        Assert.Equal(message.Id, request.OutboxMessageId);
        Assert.Equal(message.EventType, request.EventType);
        Assert.Equal(message.PayloadVersion, request.PayloadVersion);
        Assert.Equal(message.AggregateType, request.AggregateType);
        Assert.Equal(message.AggregateId, request.AggregateId);
        Assert.Equal(message.OccurredAtUtc, request.OccurredAtUtc);
        Assert.Equal("operator@example.test", request.RequestedBy);
        Assert.Equal(requestedAtUtc, request.RequestedAtUtc);
        Assert.Equal("downstream endpoint was corrected", request.Reason);
        Assert.Equal(1, request.ReplayNumber);
        Assert.Equal(0, request.PreviousAttemptCount);
        Assert.Null(request.PreviousDeadLetteredAtUtc);
        Assert.Null(request.PreviousLastError);
        Assert.Equal(requestedAtUtc, request.NextAttemptAtUtc);
    }

    [Fact]
    public void Constructor_rejects_invalid_identity_and_operator_input()
    {
        var message = CreateMessage();
        var now = new DateTimeOffset(2026, 8, 26, 16, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => CreateRequest(Guid.Empty, message, "operator", "reason", now));
        Assert.Throws<ArgumentNullException>(() => CreateRequest(Guid.NewGuid(), null!, "operator", "reason", now));
        Assert.Throws<ArgumentException>(() => CreateRequest(Guid.NewGuid(), message, " ", "reason", now));
        Assert.Throws<ArgumentException>(() => CreateRequest(Guid.NewGuid(), message, "operator", " ", now));
        Assert.Throws<ArgumentException>(() => CreateRequest(
            Guid.NewGuid(),
            message,
            new string('u', OutboxReplayRequest.MaxRequestedByLength + 1),
            "reason",
            now));
        Assert.Throws<ArgumentException>(() => CreateRequest(
            Guid.NewGuid(),
            message,
            "operator",
            new string('r', OutboxReplayRequest.MaxReasonLength + 1),
            now));
    }

    private static OutboxMessage CreateMessage()
    {
        return new OutboxMessage(
            Guid.NewGuid(),
            IncidentAlertEvents.ServerUnavailable,
            IncidentAlertEventV1.CurrentPayloadVersion,
            IncidentAlertEvents.AggregateType,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 26, 15, 0, 0, TimeSpan.Zero),
            "{}");
    }

    private static OutboxReplayRequest CreateRequest(
        Guid requestId,
        OutboxMessage message,
        string requestedBy,
        string reason,
        DateTimeOffset requestedAtUtc)
    {
        return new OutboxReplayRequest(
            requestId,
            message,
            requestedBy,
            requestedAtUtc,
            reason,
            requestedAtUtc);
    }
}
