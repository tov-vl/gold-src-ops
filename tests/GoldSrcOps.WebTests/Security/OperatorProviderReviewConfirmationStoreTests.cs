using AwesomeAssertions;
using GoldSrcOps.Web.Security;

namespace GoldSrcOps.WebTests.Security;

public sealed class OperatorProviderReviewConfirmationStoreTests
{
    [Fact]
    public void Confirmation_is_bound_to_subject_message_and_request_and_is_single_use()
    {
        var store = new OperatorProviderReviewConfirmationStore(TimeProvider.System);
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var token = store.Issue("operator", messageId, requestId);

        token.Should().HaveLength(OperatorProviderReviewConfirmationStore.TokenLength);
        store.TryConsume(token!, "other", messageId, requestId).Should().BeFalse();
        store.TryConsume(token!, "operator", messageId, requestId).Should().BeTrue();
        store.TryConsume(token!, "operator", messageId, requestId).Should().BeFalse();
    }
}
