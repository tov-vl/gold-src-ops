using AwesomeAssertions;
using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using Moq;

namespace GoldSrcOps.UnitTests.GameEvents;

public sealed class GameEventReadServiceTests
{
    [Fact]
    public async Task ListRecentRoundsAsync_uses_the_default_bounded_limit()
    {
        var serverId = Guid.NewGuid();
        IReadOnlyList<GameEventHistoryItemDto> items =
        [
            new(
                GameEventType.RoundEnded,
                new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
                "de_train",
                12,
                0)
        ];
        var repository = new Mock<IGameEventReadRepository>(MockBehavior.Strict);
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(true);
        repository
            .Setup(x => x.ListRecentRoundEndedAsync(
                serverId,
                GameEventReadService.DefaultLimit,
                CancellationToken.None))
            .ReturnsAsync(items);
        var sut = new GameEventReadService(repository.Object);

        var result = await sut.ListRecentRoundsAsync(serverId, null, CancellationToken.None);

        result.Should().Be(new GameEventHistoryDto(
            serverId,
            GameEventReadService.DefaultLimit,
            items));
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListRecentRoundsAsync_returns_null_without_querying_events_when_server_is_missing()
    {
        var serverId = Guid.NewGuid();
        var repository = new Mock<IGameEventReadRepository>(MockBehavior.Strict);
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(false);
        var sut = new GameEventReadService(repository.Object);

        var result = await sut.ListRecentRoundsAsync(serverId, 10, CancellationToken.None);

        result.Should().BeNull();
        repository.VerifyAll();
        repository.VerifyNoOtherCalls();
    }
}
