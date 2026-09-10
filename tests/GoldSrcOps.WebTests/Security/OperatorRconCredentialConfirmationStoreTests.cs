using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorRconCredentialConfirmationStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_returns_subject_bound_draft_exactly_once()
    {
        var store = new OperatorRconCredentialConfirmationStore(
            new StubTimeProvider(InitialTime));
        var draft = CreateDraft();

        var token = store.Issue("operator", draft);

        token.Should().HaveLength(OperatorRconCredentialConfirmationStore.TokenLength);
        store.TryConsume(token!, "another", draft.ServerId, out _).Should().BeFalse();
        store.TryConsume(token!, "operator", Guid.NewGuid(), out _).Should().BeFalse();
        store.TryConsume(token!, "operator", draft.ServerId, out var consumed)
            .Should().BeTrue();
        consumed.Should().Be(draft);
        store.TryConsume(token!, "operator", draft.ServerId, out _).Should().BeFalse();
    }

    [Fact]
    public void Expired_confirmation_is_reclaimed_from_bounded_store()
    {
        var timeProvider = new StubTimeProvider(InitialTime);
        var store = new OperatorRconCredentialConfirmationStore(timeProvider);
        for (var index = 0; index < OperatorRconCredentialConfirmationStore.Capacity; index++)
        {
            store.Issue("operator", CreateDraft()).Should().NotBeNull();
        }

        store.Issue("operator", CreateDraft()).Should().BeNull();
        timeProvider.Advance(OperatorRconCredentialConfirmationStore.Lifetime);

        store.Issue("operator", CreateDraft()).Should().NotBeNull();
    }

    private static OperatorRconCredentialDraft CreateDraft() => new(
        Guid.NewGuid(),
        ExpectedServerRevision: 7,
        ExpectedCredentialRevision: 3,
        SecretAlias: "primary_server");

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
