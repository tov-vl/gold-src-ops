using GoldSrcOps.Domain.Commands;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class CommandExecutionTests
{
    [Fact]
    public void Constructor_creates_pending_command_and_trims_input()
    {
        var serverId = Guid.NewGuid();
        var requestedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

        var command = new CommandExecution(
            serverId,
            ServerCommandType.Say,
            " hello players ",
            " admin ",
            requestedAtUtc);

        Assert.NotEqual(Guid.Empty, command.Id);
        Assert.Equal(serverId, command.ServerId);
        Assert.Equal(ServerCommandType.Say, command.Type);
        Assert.Equal(CommandExecutionStatus.Pending, command.Status);
        Assert.Equal("hello players", command.Payload);
        Assert.Equal("admin", command.RequestedBy);
        Assert.Equal(requestedAtUtc, command.RequestedAtUtc);
        Assert.Null(command.StartedAtUtc);
        Assert.Null(command.CompletedAtUtc);
        Assert.Null(command.ResultSummary);
        Assert.Null(command.FailureReason);
    }

    [Theory]
    [InlineData(ServerCommandType.ChangeMap)]
    [InlineData(ServerCommandType.Say)]
    [InlineData(ServerCommandType.Raw)]
    public void Constructor_requires_payload_for_non_restart_commands(ServerCommandType type)
    {
        Assert.Throws<ArgumentException>(() => new CommandExecution(
            Guid.NewGuid(),
            type,
            payload: " ",
            requestedBy: null,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Constructor_allows_restart_without_payload()
    {
        var command = new CommandExecution(
            Guid.NewGuid(),
            ServerCommandType.Restart,
            payload: null,
            requestedBy: null,
            DateTimeOffset.UtcNow);

        Assert.Null(command.Payload);
        Assert.Equal(CommandExecutionStatus.Pending, command.Status);
    }

    [Fact]
    public void MarkRunning_and_markSucceeded_update_status_timestamps_and_result()
    {
        var command = CreateCommand();
        var startedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero);
        var completedAtUtc = startedAtUtc.AddSeconds(2);

        command.MarkRunning(startedAtUtc);
        command.MarkSucceeded(completedAtUtc, " map changed ");

        Assert.Equal(CommandExecutionStatus.Succeeded, command.Status);
        Assert.Equal(startedAtUtc, command.StartedAtUtc);
        Assert.Equal(completedAtUtc, command.CompletedAtUtc);
        Assert.Equal("map changed", command.ResultSummary);
        Assert.Null(command.FailureReason);
    }

    [Fact]
    public void MarkFailed_updates_status_timestamps_and_failure_reason()
    {
        var command = CreateCommand();
        var completedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero);

        command.MarkFailed(completedAtUtc, " timeout ");

        Assert.Equal(CommandExecutionStatus.Failed, command.Status);
        Assert.Equal(completedAtUtc, command.CompletedAtUtc);
        Assert.Equal("timeout", command.FailureReason);
        Assert.Null(command.ResultSummary);
    }

    private static CommandExecution CreateCommand() =>
        new(
            Guid.NewGuid(),
            ServerCommandType.ChangeMap,
            "de_dust2",
            "admin",
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));
}
