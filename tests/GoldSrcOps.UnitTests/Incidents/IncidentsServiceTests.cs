using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Incidents;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Incidents;

public sealed class IncidentsServiceTests
{
    [Theory]
    [AutoMoqData]
    public async Task ListByServerAsync_uses_default_history_limit(
        Guid serverId,
        [Frozen] Mock<IIncidentRepository> repository,
        IncidentsService sut)
    {
        repository
            .Setup(x => x.ListByServerAsync(
                serverId,
                IncidentsService.DefaultIncidentHistoryLimit,
                CancellationToken.None))
            .ReturnsAsync([]);

        var result = await sut.ListByServerAsync(serverId, limit: null, CancellationToken.None);

        result.Should().BeEmpty();
        repository.Verify(x => x.ListByServerAsync(
            serverId,
            IncidentsService.DefaultIncidentHistoryLimit,
            CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task ListByServerAsync_clamps_history_limit(
        Guid serverId,
        [Frozen] Mock<IIncidentRepository> repository,
        IncidentsService sut)
    {
        repository
            .Setup(x => x.ListByServerAsync(
                serverId,
                IncidentsService.MaxIncidentHistoryLimit,
                CancellationToken.None))
            .ReturnsAsync([]);

        var result = await sut.ListByServerAsync(
            serverId,
            IncidentsService.MaxIncidentHistoryLimit + 1,
            CancellationToken.None);

        result.Should().BeEmpty();
        repository.Verify(x => x.ListByServerAsync(
            serverId,
            IncidentsService.MaxIncidentHistoryLimit,
            CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }
}
