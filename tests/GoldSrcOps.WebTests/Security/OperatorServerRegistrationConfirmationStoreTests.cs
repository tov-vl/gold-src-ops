using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorServerRegistrationConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_returns_the_bound_draft_exactly_once()
    {
        var store = new OperatorServerRegistrationConfirmationStore(
            new StubTimeProvider(InitialTime));
        var draft = CreateDraft();

        var token = store.Issue("operator", draft);

        token.Should().HaveLength(OperatorServerRegistrationConfirmationStore.TokenLength);
        store.TryConsume(token!, "operator", out var consumedDraft).Should().BeTrue();
        consumedDraft.Should().Be(draft);
        store.TryConsume(token!, "operator", out _).Should().BeFalse();
    }

    [Fact]
    public void Confirmation_is_bound_to_the_authenticated_subject()
    {
        var store = new OperatorServerRegistrationConfirmationStore(
            new StubTimeProvider(InitialTime));
        var draft = CreateDraft();
        var token = store.Issue("operator", draft);

        store.TryConsume(token!, "another-operator", out _).Should().BeFalse();
        store.TryConsume(token!, "operator", out var consumedDraft).Should().BeTrue();
        consumedDraft.Should().Be(draft);
    }

    [Fact]
    public void Expired_confirmation_cannot_be_consumed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorServerRegistrationConfirmationStore(timeProvider);
        var token = store.Issue("operator", CreateDraft());
        timeProvider.Advance(OperatorServerRegistrationConfirmationStore.Lifetime);

        var consumed = store.TryConsume(token!, "operator", out _);

        consumed.Should().BeFalse();
    }

    [Fact]
    public void Capacity_is_bounded_and_expired_entries_are_reclaimed()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorServerRegistrationConfirmationStore(timeProvider);

        for (var index = 0; index < OperatorServerRegistrationConfirmationStore.Capacity; index++)
        {
            store.Issue("operator", CreateDraft()).Should().NotBeNull();
        }

        store.Issue("operator", CreateDraft()).Should().BeNull();
        timeProvider.Advance(OperatorServerRegistrationConfirmationStore.Lifetime);
        store.Issue("operator", CreateDraft()).Should().NotBeNull();
    }

    private static OperatorServerRegistrationDraft CreateDraft() => new(
        Guid.NewGuid(),
        "Public server",
        "game.example.test",
        27015,
        27016,
        60,
        "Paused registration");

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
