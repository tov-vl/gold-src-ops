using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Alerts;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Alerts;

public sealed class AlertDeliveryReadServiceTests
{
    [Theory]
    [AutoMoqData]
    public async Task ListDeadLettersAsync_returns_a_stable_next_position(
        [Frozen] Mock<IAlertDeliveryReadRepository> repository,
        AlertDeliveryReadService sut)
    {
        var deadLetteredAtUtc = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<DeadLetterListItemDto> rows =
        [
            CreateItem(deadLetteredAtUtc, deadLetteredAtUtc.AddMinutes(-1)),
            CreateItem(deadLetteredAtUtc, deadLetteredAtUtc.AddMinutes(-2)),
            CreateItem(deadLetteredAtUtc.AddHours(-1), deadLetteredAtUtc.AddMinutes(-3))
        ];
        repository
            .Setup(static x => x.ListDeadLettersAsync(
                null,
                3,
                CancellationToken.None))
            .ReturnsAsync(rows);

        var result = await sut.ListDeadLettersAsync(
            position: null,
            limit: 2,
            CancellationToken.None);

        result.Limit.Should().Be(2);
        result.Items.Should().Equal(rows.Take(2));
        result.NextPosition.Should().Be(new DeadLetterPagePosition(
            rows[1].DeadLetteredAtUtc,
            rows[1].OccurredAtUtc,
            rows[1].EventId));
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task ListDeadLettersAsync_uses_the_default_limit_and_omits_a_terminal_cursor(
        [Frozen] Mock<IAlertDeliveryReadRepository> repository,
        AlertDeliveryReadService sut)
    {
        repository
            .Setup(static x => x.ListDeadLettersAsync(
                null,
                AlertDeliveryReadService.DefaultDeadLetterLimit + 1,
                CancellationToken.None))
            .ReturnsAsync([]);

        var result = await sut.ListDeadLettersAsync(
            position: null,
            limit: null,
            CancellationToken.None);

        result.Should().Be(new DeadLetterPageDto(
            AlertDeliveryReadService.DefaultDeadLetterLimit,
            [],
            NextPosition: null));
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineAutoMoqData(0)]
    [InlineAutoMoqData(AlertDeliveryReadService.MaxDeadLetterLimit + 1)]
    public async Task ListDeadLettersAsync_rejects_out_of_range_limits(
        int limit,
        [Frozen] Mock<IAlertDeliveryReadRepository> repository,
        AlertDeliveryReadService sut)
    {
        var act = () => sut.ListDeadLettersAsync(
            position: null,
            limit,
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        repository.VerifyNoOtherCalls();
    }

    private static DeadLetterListItemDto CreateItem(
        DateTimeOffset? deadLetteredAtUtc,
        DateTimeOffset occurredAtUtc) =>
        new(
            Guid.NewGuid(),
            IncidentAlertEvents.ServerUnavailable,
            PayloadVersion: 1,
            IncidentAlertEvents.AggregateType,
            Guid.NewGuid(),
            occurredAtUtc,
            AttemptCount: 3,
            ReplayCount: 0,
            deadLetteredAtUtc,
            LastError: "permanent HTTP 400 response");
}
