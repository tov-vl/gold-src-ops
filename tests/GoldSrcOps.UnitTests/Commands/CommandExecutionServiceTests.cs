using AutoFixture.Xunit2;
using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Common;
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

    [Fact]
    public async Task DispatchAsync_marks_command_succeeded_and_builds_rcon_request()
    {
        var command = CreateCommand(ServerCommandType.Say, "hello players");
        var repository = new InMemoryCommandExecutionRepository(
            new CommandExecutionDispatchContext(
                command,
                Host: "127.0.0.1",
                RconPort: 27015,
                CredentialSecretReference: "dev-secrets://goldsrcops/server/rcon"));
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("accepted"));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandExecutionService(repository, executor, clock);

        var result = await sut.DispatchAsync(command.Id, CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionDispatchResultKind.Dispatched);
        result.Command.Should().BeEquivalentTo(new
        {
            command.Id,
            command.ServerId,
            Type = ServerCommandType.Say,
            Status = CommandExecutionStatus.Succeeded,
            Payload = "hello players",
            StartedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            CompletedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero),
            ResultSummary = "accepted",
            FailureReason = (string?)null
        });
        repository.SaveCount.Should().Be(2);
        executor.CallCount.Should().Be(1);
        executor.LastRequest.Should().BeEquivalentTo(new
        {
            CommandId = command.Id,
            command.ServerId,
            Host = "127.0.0.1",
            Port = 27015,
            CredentialSecretReference = "dev-secrets://goldsrcops/server/rcon",
            Type = ServerCommandType.Say,
            CommandText = "say hello players"
        });
    }

    [Fact]
    public async Task DispatchAsync_marks_command_failed_when_executor_returns_failure()
    {
        var command = CreateCommand(ServerCommandType.Raw, "amx_kick #42");
        var repository = new InMemoryCommandExecutionRepository(
            new CommandExecutionDispatchContext(
                command,
                Host: "127.0.0.1",
                RconPort: 27015,
                CredentialSecretReference: "dev-secrets://goldsrcops/server/rcon"));
        var executor = new CapturingRconCommandExecutor(
            RconCommandExecutionResult.Failed("RCON command rejected for dev-secrets://goldsrcops/server/rcon."));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandExecutionService(repository, executor, clock);

        var result = await sut.DispatchAsync(command.Id, CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionDispatchResultKind.Dispatched);
        result.Command.Should().NotBeNull();
        result.Command!.Status.Should().Be(CommandExecutionStatus.Failed);
        result.Command.FailureReason.Should().Be("RCON command rejected for [credential].");
        result.Command.FailureReason.Should().NotContain("dev-secrets://goldsrcops/server/rcon");
        result.Command.ResultSummary.Should().BeNull();
        executor.LastRequest!.CommandText.Should().Be("amx_kick #42");
        repository.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task DispatchAsync_marks_command_failed_on_timeout_without_leaking_exception_message()
    {
        var command = CreateCommand(ServerCommandType.ChangeMap, "de_dust2");
        var repository = new InMemoryCommandExecutionRepository(
            new CommandExecutionDispatchContext(
                command,
                Host: "127.0.0.1",
                RconPort: 27015,
                CredentialSecretReference: "dev-secrets://goldsrcops/server/rcon"));
        var executor = new CapturingRconCommandExecutor((_, _) =>
            throw new TimeoutException("timeout while using super-secret-rcon-password"));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandExecutionService(repository, executor, clock);

        var result = await sut.DispatchAsync(command.Id, CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionDispatchResultKind.Dispatched);
        result.Command.Should().NotBeNull();
        result.Command!.Status.Should().Be(CommandExecutionStatus.Failed);
        result.Command.FailureReason.Should().Be("RCON command timed out.");
        result.Command.FailureReason.Should().NotContain("super-secret-rcon-password");
        executor.LastRequest!.CommandText.Should().Be("changelevel de_dust2");
        repository.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task DispatchAsync_marks_command_failed_when_rcon_port_is_missing()
    {
        var command = CreateCommand(ServerCommandType.Restart, payload: null);
        var repository = new InMemoryCommandExecutionRepository(
            new CommandExecutionDispatchContext(
                command,
                Host: "127.0.0.1",
                RconPort: null,
                CredentialSecretReference: "dev-secrets://goldsrcops/server/rcon"));
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("should not execute"));
        var clock = new SequenceClock(new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero));
        var sut = new CommandExecutionService(repository, executor, clock);

        var result = await sut.DispatchAsync(command.Id, CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionDispatchResultKind.Dispatched);
        result.Command.Should().NotBeNull();
        result.Command!.Status.Should().Be(CommandExecutionStatus.Failed);
        result.Command.FailureReason.Should().Be("RCON port is not configured.");
        executor.CallCount.Should().Be(0);
        repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_returns_not_pending_without_calling_executor()
    {
        var command = CreateCommand(ServerCommandType.Restart, payload: null);
        command.MarkSucceeded(new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero), "already done");
        var repository = new InMemoryCommandExecutionRepository(
            new CommandExecutionDispatchContext(
                command,
                Host: "127.0.0.1",
                RconPort: 27015,
                CredentialSecretReference: "dev-secrets://goldsrcops/server/rcon"));
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("should not execute"));
        var clock = new SequenceClock(new DateTimeOffset(2026, 4, 25, 12, 2, 0, TimeSpan.Zero));
        var sut = new CommandExecutionService(repository, executor, clock);

        var result = await sut.DispatchAsync(command.Id, CancellationToken.None);

        result.Kind.Should().Be(CommandExecutionDispatchResultKind.NotPending);
        result.Command.Should().NotBeNull();
        result.Command!.Status.Should().Be(CommandExecutionStatus.Succeeded);
        executor.CallCount.Should().Be(0);
        repository.SaveCount.Should().Be(0);
    }

    private static CommandExecution CreateCommand(ServerCommandType type, string? payload) =>
        new(
            Guid.NewGuid(),
            type,
            payload,
            requestedBy: "admin",
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));

    private sealed class InMemoryCommandExecutionRepository : ICommandExecutionRepository
    {
        private readonly CommandExecutionDispatchContext? _dispatchContext;

        public InMemoryCommandExecutionRepository(CommandExecutionDispatchContext? dispatchContext)
        {
            _dispatchContext = dispatchContext;
        }

        public int SaveCount { get; private set; }

        public Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HasCredentialAsync(
            Guid serverId,
            ServerCredentialKind kind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(CommandExecution command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandExecution?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandExecutionDispatchContext?> GetDispatchContextAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                _dispatchContext?.Command.Id == id
                    ? _dispatchContext
                    : null);
        }

        public Task<IReadOnlyList<CommandExecution>> ListByServerAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class SequenceClock : IClock
    {
        private readonly Queue<DateTimeOffset> _values;
        private DateTimeOffset _last;

        public SequenceClock(params DateTimeOffset[] values)
        {
            _values = new Queue<DateTimeOffset>(values);
            _last = values[^1];
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                if (_values.Count > 0)
                {
                    _last = _values.Dequeue();
                }

                return _last;
            }
        }
    }
}
