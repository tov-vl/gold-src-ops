using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorServerMonitoringConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_can_be_consumed_exactly_once()
    {
        var store = new OperatorServerMonitoringConfirmationStore(
            new StubTimeProvider(InitialTime));
        var serverId = Guid.NewGuid();

        var token = store.Issue(
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable);

        token.Should().HaveLength(OperatorServerMonitoringConfirmationStore.TokenLength);
        store.TryConsume(
            token!,
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable).Should().BeTrue();
        store.TryConsume(
            token!,
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable).Should().BeFalse();
    }

    [Fact]
    public void Confirmation_is_bound_to_subject_server_and_action()
    {
        var store = new OperatorServerMonitoringConfirmationStore(
            new StubTimeProvider(InitialTime));
        var serverId = Guid.NewGuid();
        var token = store.Issue(
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable);

        store.TryConsume(
            token!,
            "another-operator",
            serverId,
            OperatorServerMonitoringAction.Disable).Should().BeFalse();
        store.TryConsume(
            token!,
            "operator",
            Guid.NewGuid(),
            OperatorServerMonitoringAction.Disable).Should().BeFalse();
        store.TryConsume(
            token!,
            "operator",
            serverId,
            OperatorServerMonitoringAction.Enable).Should().BeFalse();
        store.TryConsume(
            token!,
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable).Should().BeTrue();
    }

    [Fact]
    public void Expired_confirmation_cannot_be_consumed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorServerMonitoringConfirmationStore(timeProvider);
        var serverId = Guid.NewGuid();
        var token = store.Issue(
            "operator",
            serverId,
            OperatorServerMonitoringAction.Enable);
        timeProvider.Advance(OperatorServerMonitoringConfirmationStore.Lifetime);

        var consumed = store.TryConsume(
            token!,
            "operator",
            serverId,
            OperatorServerMonitoringAction.Enable);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Capacity_is_bounded_and_expired_entries_are_reclaimed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorServerMonitoringConfirmationStore(timeProvider);
        var serverId = Guid.NewGuid();

        for (var index = 0; index < OperatorServerMonitoringConfirmationStore.Capacity; index++)
        {
            store.Issue(
                "operator",
                serverId,
                OperatorServerMonitoringAction.Disable).Should().NotBeNull();
        }

        store.Issue(
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable).Should().BeNull();
        timeProvider.Advance(OperatorServerMonitoringConfirmationStore.Lifetime);
        store.Issue(
            "operator",
            serverId,
            OperatorServerMonitoringAction.Disable).Should().NotBeNull();
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
