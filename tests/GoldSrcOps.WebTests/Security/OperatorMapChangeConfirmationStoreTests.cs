using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorMapChangeConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_returns_normalized_subject_bound_draft_exactly_once()
    {
        var store = new OperatorMapChangeConfirmationStore(
            new StubTimeProvider(InitialTime));
        var serverId = Guid.NewGuid();

        var token = store.Issue(
            "operator",
            new OperatorMapChangeDraft(serverId, "  de_dust2  "));

        token.Should().HaveLength(OperatorMapChangeConfirmationStore.TokenLength);
        store.TryConsume(token!, "another", serverId, out _).Should().BeFalse();
        store.TryConsume(token!, "operator", Guid.NewGuid(), out _).Should().BeFalse();
        store.TryConsume(token!, "operator", serverId, out var consumed).Should().BeTrue();
        consumed.Should().Be(new OperatorMapChangeDraft(serverId, "de_dust2"));
        store.TryConsume(token!, "operator", serverId, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("de_dust2;quit")]
    [InlineData("../de_dust2")]
    [InlineData("de dust2")]
    public void Unsafe_map_name_is_rejected(string map)
    {
        var store = new OperatorMapChangeConfirmationStore(
            new StubTimeProvider(InitialTime));

        var action = () => store.Issue(
            "operator",
            new OperatorMapChangeDraft(Guid.NewGuid(), map));

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Expired_confirmation_is_reclaimed_from_bounded_store()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorMapChangeConfirmationStore(timeProvider);
        var draft = new OperatorMapChangeDraft(Guid.NewGuid(), "de_dust2");
        for (var index = 0; index < OperatorMapChangeConfirmationStore.Capacity; index++)
        {
            store.Issue("operator", draft).Should().NotBeNull();
        }

        store.Issue("operator", draft).Should().BeNull();
        timeProvider.Advance(OperatorMapChangeConfirmationStore.Lifetime);

        store.Issue("operator", draft).Should().NotBeNull();
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
