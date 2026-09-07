using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorCommandConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_can_be_consumed_exactly_once()
    {
        var store = new OperatorCommandConfirmationStore(new StubTimeProvider(InitialTime));
        var serverId = Guid.NewGuid();

        var token = store.Issue("operator", serverId);

        token.Should().HaveLength(OperatorCommandConfirmationStore.TokenLength);
        store.TryConsume(token!, "operator", serverId).Should().BeTrue();
        store.TryConsume(token!, "operator", serverId).Should().BeFalse();
    }

    [Fact]
    public void Confirmation_is_bound_to_subject_and_server()
    {
        var store = new OperatorCommandConfirmationStore(new StubTimeProvider(InitialTime));
        var serverId = Guid.NewGuid();
        var token = store.Issue("operator", serverId);

        store.TryConsume(token!, "another-operator", serverId).Should().BeFalse();
        store.TryConsume(token!, "operator", Guid.NewGuid()).Should().BeFalse();
        store.TryConsume(token!, "operator", serverId).Should().BeTrue();
    }

    [Fact]
    public void Expired_confirmation_cannot_be_consumed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorCommandConfirmationStore(timeProvider);
        var serverId = Guid.NewGuid();
        var token = store.Issue("operator", serverId);
        timeProvider.Advance(OperatorCommandConfirmationStore.Lifetime);

        var consumed = store.TryConsume(token!, "operator", serverId);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Capacity_is_bounded_and_expired_entries_are_reclaimed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorCommandConfirmationStore(timeProvider);
        var serverId = Guid.NewGuid();

        for (var index = 0; index < OperatorCommandConfirmationStore.Capacity; index++)
        {
            store.Issue("operator", serverId).Should().NotBeNull();
        }

        store.Issue("operator", serverId).Should().BeNull();
        timeProvider.Advance(OperatorCommandConfirmationStore.Lifetime);
        store.Issue("operator", serverId).Should().NotBeNull();
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
