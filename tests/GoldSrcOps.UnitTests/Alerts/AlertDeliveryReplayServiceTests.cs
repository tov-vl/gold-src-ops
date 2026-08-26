using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.Application.Common;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class AlertDeliveryReplayServiceTests
{
    [Theory]
    [AutoMoqData]
    public async Task ReplayAsync_normalizes_operator_input_and_uses_utc_clock(
        Guid requestId,
        Guid eventId,
        [Frozen] Mock<IAlertDeliveryReplayRepository> repository,
        [Frozen] Mock<IClock> clock,
        AlertDeliveryReplayService sut)
    {
        var now = new DateTimeOffset(2026, 8, 26, 18, 30, 0, TimeSpan.FromHours(3));
        var expected = DeadLetterReplayResult.EventNotFound();
        clock.SetupGet(static value => value.UtcNow).Returns(now);
        repository
            .Setup(x => x.ReplayAsync(
                requestId,
                eventId,
                "operator@example.test",
                now.ToUniversalTime(),
                "downstream endpoint was corrected",
                CancellationToken.None))
            .ReturnsAsync(expected);

        var result = await sut.ReplayAsync(
            new DeadLetterReplayCommand(
                requestId,
                eventId,
                " operator@example.test ",
                " downstream endpoint was corrected "),
            CancellationToken.None);

        result.Should().BeSameAs(expected);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task GetReplayAsync_delegates_to_repository(
        Guid requestId,
        DeadLetterReplayRecordDto expected,
        [Frozen] Mock<IAlertDeliveryReplayRepository> repository,
        AlertDeliveryReplayService sut)
    {
        repository
            .Setup(x => x.GetReplayAsync(requestId, CancellationToken.None))
            .ReturnsAsync(expected);

        var result = await sut.GetReplayAsync(requestId, CancellationToken.None);

        result.Should().BeSameAs(expected);
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeReason_rejects_empty_values(string? reason)
    {
        var success = AlertDeliveryReplayService.TryNormalizeReason(
            reason,
            out var normalizedReason);

        success.Should().BeFalse();
        normalizedReason.Should().BeEmpty();
    }

    [Fact]
    public void TryNormalizeReason_trims_a_valid_value()
    {
        var success = AlertDeliveryReplayService.TryNormalizeReason(
            " maintenance completed ",
            out var normalizedReason);

        success.Should().BeTrue();
        normalizedReason.Should().Be("maintenance completed");
    }

    [Fact]
    public async Task ReplayAsync_rejects_invalid_input_before_repository_call()
    {
        var repository = new Mock<IAlertDeliveryReplayRepository>(MockBehavior.Strict);
        var clock = new Mock<IClock>(MockBehavior.Strict);
        var sut = new AlertDeliveryReplayService(repository.Object, clock.Object);

        var emptyRequestId = () => sut.ReplayAsync(
            new DeadLetterReplayCommand(
                Guid.Empty,
                Guid.NewGuid(),
                "operator",
                "reason"),
            CancellationToken.None);
        var longReason = () => sut.ReplayAsync(
            new DeadLetterReplayCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "operator",
                new string('r', AlertDeliveryReplayService.MaxReasonLength + 1)),
            CancellationToken.None);

        await emptyRequestId.Should().ThrowAsync<ArgumentException>();
        await longReason.Should().ThrowAsync<ArgumentException>();
        repository.VerifyNoOtherCalls();
        clock.VerifyNoOtherCalls();
    }
}
