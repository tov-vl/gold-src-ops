using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.UnitTests.Helpers;
using Moq;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class CommandExecutionServiceTests
{
    [Theory]
    [AutoMoqData]
    public async Task QueueAsync_returns_server_not_found_when_server_does_not_exist(
        Guid serverId,
        [Frozen] Mock<ICommandExecutionRepository> repository,
        CommandExecutionService sut)
    {
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(false);

        var result = await sut.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.Say, "hello", "admin"),
            CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionCreateResultKind.ServerNotFound);
        result.Command.Should().BeNull();
        repository.Verify(x => x.ServerExistsAsync(serverId, CancellationToken.None), Times.Once);
        repository.Verify(x => x.HasCredentialAsync(
            It.IsAny<Guid>(),
            It.IsAny<ServerCredentialKind>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.AddAsync(It.IsAny<CommandExecution>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task QueueAsync_returns_missing_credential_when_rcon_credential_is_not_configured(
        Guid serverId,
        [Frozen] Mock<ICommandExecutionRepository> repository,
        CommandExecutionService sut)
    {
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(true);
        repository
            .Setup(x => x.HasCredentialAsync(serverId, ServerCredentialKind.RconPassword, CancellationToken.None))
            .ReturnsAsync(false);

        var result = await sut.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.Say, "hello", "admin"),
            CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionCreateResultKind.MissingRconCredential);
        result.Command.Should().BeNull();
        repository.Verify(x => x.ServerExistsAsync(serverId, CancellationToken.None), Times.Once);
        repository.Verify(x => x.HasCredentialAsync(serverId, ServerCredentialKind.RconPassword, CancellationToken.None), Times.Once);
        repository.Verify(x => x.AddAsync(It.IsAny<CommandExecution>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [AutoMoqData]
    public async Task QueueAsync_persists_pending_command_when_server_and_rcon_credential_exist(
        Guid serverId,
        [Frozen] Mock<ICommandExecutionRepository> repository,
        [Frozen] Mock<IClock> clock,
        CommandExecutionService sut)
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        CommandExecution? persisted = null;
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(true);
        repository
            .Setup(x => x.HasCredentialAsync(serverId, ServerCredentialKind.RconPassword, CancellationToken.None))
            .ReturnsAsync(true);
        repository
            .Setup(x => x.AddAsync(It.IsAny<CommandExecution>(), CancellationToken.None))
            .Callback<CommandExecution, CancellationToken>((command, _) => persisted = command)
            .Returns(Task.CompletedTask);
        repository
            .Setup(x => x.SaveChangesAsync(CancellationToken.None))
            .Returns(Task.CompletedTask);
        clock
            .SetupGet(x => x.UtcNow)
            .Returns(now);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.QueueAsync(
            serverId,
            new CreateCommandExecutionCommand(ServerCommandType.ChangeMap, " de_dust2 ", " admin "),
            CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionCreateResultKind.Created);
        result.Command.Should().NotBeNull();
        result.Command.Should().BeEquivalentTo(new
        {
            ServerId = serverId,
            Type = ServerCommandType.ChangeMap,
            Status = CommandExecutionStatus.Pending,
            Payload = "de_dust2",
            RequestedBy = "admin",
            RequestedAtUtc = now,
            StartedAtUtc = (DateTimeOffset?)null,
            CompletedAtUtc = (DateTimeOffset?)null,
            ResultSummary = (string?)null,
            FailureReason = (string?)null
        });
        persisted.Should().NotBeNull();
        repository.Verify(x => x.ServerExistsAsync(serverId, CancellationToken.None), Times.Once);
        repository.Verify(x => x.HasCredentialAsync(serverId, ServerCredentialKind.RconPassword, CancellationToken.None), Times.Once);
        repository.Verify(x => x.AddAsync(It.IsAny<CommandExecution>(), CancellationToken.None), Times.Once);
        repository.Verify(x => x.SaveChangesAsync(CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.commands.queued" &&
            metric.Value == 1 &&
            HasTag(metric, "command_type", "ChangeMap"));
    }

    [Theory]
    [AutoMoqData]
    public async Task ListByServerAsync_clamps_history_limit(
        Guid serverId,
        [Frozen] Mock<ICommandExecutionRepository> repository,
        CommandExecutionService sut)
    {
        repository
            .Setup(x => x.ServerExistsAsync(serverId, CancellationToken.None))
            .ReturnsAsync(true);
        repository
            .Setup(x => x.ListByServerAsync(
                serverId,
                CommandExecutionService.MaxCommandHistoryLimit,
                CancellationToken.None))
            .ReturnsAsync([]);

        var result = await sut.ListByServerAsync(
            serverId,
            limit: CommandExecutionService.MaxCommandHistoryLimit + 1,
            CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
        repository.Verify(x => x.ServerExistsAsync(serverId, CancellationToken.None), Times.Once);
        repository.Verify(x => x.ListByServerAsync(
            serverId,
            CommandExecutionService.MaxCommandHistoryLimit,
            CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);

}
