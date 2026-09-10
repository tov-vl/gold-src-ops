using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorServerUpdateConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_returns_the_bound_draft_exactly_once()
    {
        var store = new OperatorServerUpdateConfirmationStore(
            new StubTimeProvider(InitialTime));
        var draft = CreateDraft();

        var token = store.Issue("operator", draft);

        token.Should().HaveLength(OperatorServerUpdateConfirmationStore.TokenLength);
        store.TryConsume(token!, "operator", draft.ServerId, out var consumedDraft)
            .Should().BeTrue();
        consumedDraft.Should().Be(draft);
        store.TryConsume(token!, "operator", draft.ServerId, out _).Should().BeFalse();
    }

    [Fact]
    public void Confirmation_is_bound_to_subject_and_server()
    {
        var store = new OperatorServerUpdateConfirmationStore(
            new StubTimeProvider(InitialTime));
        var draft = CreateDraft();
        var token = store.Issue("operator", draft);

        store.TryConsume(token!, "another-operator", draft.ServerId, out _).Should().BeFalse();
        store.TryConsume(token!, "operator", Guid.NewGuid(), out _).Should().BeFalse();
        store.TryConsume(token!, "operator", draft.ServerId, out var consumedDraft)
            .Should().BeTrue();
        consumedDraft.Should().Be(draft);
    }

    [Fact]
    public void Expired_confirmation_cannot_be_consumed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorServerUpdateConfirmationStore(timeProvider);
        var draft = CreateDraft();
        var token = store.Issue("operator", draft);
        timeProvider.Advance(OperatorServerUpdateConfirmationStore.Lifetime);

        var consumed = store.TryConsume(token!, "operator", draft.ServerId, out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Capacity_is_bounded_and_expired_entries_are_reclaimed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorServerUpdateConfirmationStore(timeProvider);

        for (var index = 0; index < OperatorServerUpdateConfirmationStore.Capacity; index++)
        {
            store.Issue("operator", CreateDraft()).Should().NotBeNull();
        }

        store.Issue("operator", CreateDraft()).Should().BeNull();
        timeProvider.Advance(OperatorServerUpdateConfirmationStore.Lifetime);
        store.Issue("operator", CreateDraft()).Should().NotBeNull();
    }

    private static OperatorServerUpdateDraft CreateDraft() => new(
        Guid.NewGuid(),
        ExpectedRevision: 7,
        "Public server",
        "game.example.test",
        QueryPort: 27015,
        RconPort: 27016,
        PollIntervalSeconds: 60,
        Notes: "Paused update");

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
