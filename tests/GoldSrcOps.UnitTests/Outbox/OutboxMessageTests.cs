using GoldSrcOps.Infrastructure.Persistence.Outbox;

namespace GoldSrcOps.UnitTests.Outbox;

public sealed class OutboxMessageTests
{
    [Fact]
    public void Constructor_creates_pending_message_and_trims_identifiers()
    {
        var messageId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        var occurredAtUtc = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        const string payload = """{"eventId":"test"}""";

        var message = new OutboxMessage(
            messageId,
            " server.availability.unavailable ",
            1,
            " availability-incident ",
            aggregateId,
            occurredAtUtc,
            payload);

        Assert.Equal(messageId, message.Id);
        Assert.Equal("server.availability.unavailable", message.EventType);
        Assert.Equal(1, message.PayloadVersion);
        Assert.Equal("availability-incident", message.AggregateType);
        Assert.Equal(aggregateId, message.AggregateId);
        Assert.Equal(occurredAtUtc, message.OccurredAtUtc);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.Equal(0, message.AttemptCount);
        Assert.Equal(occurredAtUtc, message.NextAttemptAtUtc);
        Assert.Null(message.ClaimId);
        Assert.Null(message.ClaimedAtUtc);
        Assert.Null(message.ProcessedAtUtc);
        Assert.Null(message.LastError);
    }

    [Fact]
    public void Constructor_rejects_invalid_required_values()
    {
        var occurredAtUtc = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => CreateMessage(id: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateMessage(payloadVersion: 0));
        Assert.Throws<ArgumentException>(() => CreateMessage(aggregateId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateMessage(eventType: " "));
        Assert.Throws<ArgumentException>(() => CreateMessage(aggregateType: " "));
        Assert.Throws<ArgumentException>(() => CreateMessage(payload: " "));
        Assert.Throws<ArgumentException>(() => new OutboxMessage(
            Guid.NewGuid(),
            new string('e', OutboxMessage.MaxEventTypeLength + 1),
            1,
            "availability-incident",
            Guid.NewGuid(),
            occurredAtUtc,
            "{}"));
    }

    private static OutboxMessage CreateMessage(
        Guid? id = null,
        string eventType = "server.availability.unavailable",
        short payloadVersion = 1,
        string aggregateType = "availability-incident",
        Guid? aggregateId = null,
        string payload = "{}") =>
        new(
            id ?? Guid.NewGuid(),
            eventType,
            payloadVersion,
            aggregateType,
            aggregateId ?? Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
            payload);
}
