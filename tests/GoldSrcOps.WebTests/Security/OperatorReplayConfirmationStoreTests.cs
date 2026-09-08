using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorReplayConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_can_be_consumed_exactly_once()
    {
        var store = new OperatorReplayConfirmationStore(new StubTimeProvider(InitialTime));
        var eventId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        var token = store.Issue("operator", eventId, requestId);

        token.Should().HaveLength(OperatorReplayConfirmationStore.TokenLength);
        store.TryConsume(token!, "operator", eventId, requestId).Should().BeTrue();
        store.TryConsume(token!, "operator", eventId, requestId).Should().BeFalse();
    }

    [Fact]
    public void Confirmation_is_bound_to_subject_event_and_request()
    {
        var store = new OperatorReplayConfirmationStore(new StubTimeProvider(InitialTime));
        var eventId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var token = store.Issue("operator", eventId, requestId);

        store.TryConsume(token!, "another-operator", eventId, requestId).Should().BeFalse();
        store.TryConsume(token!, "operator", Guid.NewGuid(), requestId).Should().BeFalse();
        store.TryConsume(token!, "operator", eventId, Guid.NewGuid()).Should().BeFalse();
        store.TryConsume(token!, "operator", eventId, requestId).Should().BeTrue();
    }

    [Fact]
    public void Expired_confirmation_cannot_be_consumed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorReplayConfirmationStore(timeProvider);
        var eventId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var token = store.Issue("operator", eventId, requestId);
        timeProvider.Advance(OperatorReplayConfirmationStore.Lifetime);

        var consumed = store.TryConsume(token!, "operator", eventId, requestId);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Capacity_is_bounded_and_expired_entries_are_reclaimed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorReplayConfirmationStore(timeProvider);
        var eventId = Guid.NewGuid();

        for (var index = 0; index < OperatorReplayConfirmationStore.Capacity; index++)
        {
            store.Issue("operator", eventId, Guid.NewGuid()).Should().NotBeNull();
        }

        store.Issue("operator", eventId, Guid.NewGuid()).Should().BeNull();
        timeProvider.Advance(OperatorReplayConfirmationStore.Lifetime);
        store.Issue("operator", eventId, Guid.NewGuid()).Should().NotBeNull();
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
