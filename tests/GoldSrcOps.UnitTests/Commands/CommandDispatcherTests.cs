using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Domain.Servers;
using GoldSrcOps.UnitTests.Helpers;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class CommandDispatcherTests
{
    [Fact]
    public async Task DispatchNextAsync_marks_command_succeeded_and_builds_rcon_request()
    {
        var command = CreateCommand(ServerCommandType.Say, "hello players");
        var repository = CreateRepository(command);
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded("accepted"));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandDispatcher(repository, executor, clock);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Should().BeEquivalentTo(new
        {
            Kind = CommandDispatchAttemptResultKind.Completed,
            CommandId = (Guid?)command.Id,
            ServerId = (Guid?)command.ServerId,
            Status = (CommandExecutionStatus?)CommandExecutionStatus.Succeeded
        });
        command.Should().BeEquivalentTo(new
        {
            Status = CommandExecutionStatus.Succeeded,
            StartedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            CompletedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero),
            ResultSummary = "accepted",
            FailureReason = (string?)null
        });
        repository.CompleteCount.Should().Be(1);
        executor.CallCount.Should().Be(1);
        executor.LastRequest.Should().BeEquivalentTo(new
        {
            CommandId = command.Id,
            command.ServerId,
            Host = "127.0.0.1",
            Port = 27015,
            CredentialSecretReference = "rcon-secret://server_rcon",
            Type = ServerCommandType.Say,
            CommandText = "say hello players"
        });
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.commands.dispatched" &&
            metric.Value == 1 &&
            HasTag(metric, "command_type", "Say"));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.commands.completed" &&
            metric.Value == 1 &&
            HasTag(metric, "command_type", "Say") &&
            HasTag(metric, "result", "succeeded"));
    }

    [Fact]
    public async Task DispatchNextAsync_marks_command_failed_when_executor_returns_failure()
    {
        var command = CreateCommand(ServerCommandType.Raw, "amx_kick #42");
        var repository = CreateRepository(command);
        var executor = new CapturingRconCommandExecutor(
            RconCommandExecutionResult.Failed("Rejected for rcon-secret://server_rcon."));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandDispatcher(repository, executor, clock);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Kind.Should().Be(CommandDispatchAttemptResultKind.Completed);
        result.Status.Should().Be(CommandExecutionStatus.Failed);
        command.FailureReason.Should().Be("Rejected for [credential].");
        command.ResultSummary.Should().BeNull();
        repository.CompleteCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchNextAsync_records_auth_failed_metric()
    {
        var command = CreateCommand(ServerCommandType.Restart, payload: null);
        var repository = CreateRepository(command);
        var executor = new CapturingRconCommandExecutor(
            RconCommandExecutionResult.AuthenticationFailed("RCON authentication failed."));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandDispatcher(repository, executor, clock);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Failed);
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.commands.completed" &&
            metric.Value == 1 &&
            HasTag(metric, "command_type", "Restart") &&
            HasTag(metric, "result", "auth_failed"));
    }

    [Fact]
    public async Task DispatchNextAsync_marks_command_failed_on_timeout_without_leaking_exception_message()
    {
        var command = CreateCommand(ServerCommandType.ChangeMap, "de_dust2");
        var repository = CreateRepository(command);
        var executor = new CapturingRconCommandExecutor((_, _) =>
            throw new TimeoutException("timeout while using super-secret-rcon-password"));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandDispatcher(repository, executor, clock);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Failed);
        command.FailureReason.Should().Be("RCON command timed out.");
        command.FailureReason.Should().NotContain("super-secret-rcon-password");
    }

    [Fact]
    public async Task DispatchNextAsync_marks_command_failed_when_rcon_port_is_missing()
    {
        var command = CreateCommand(ServerCommandType.Restart, payload: null);
        var repository = CreateRepository(command, rconPort: null);
        var executor = new CapturingRconCommandExecutor(
            RconCommandExecutionResult.Succeeded("should not execute"));
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandDispatcher(repository, executor, clock);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Status.Should().Be(CommandExecutionStatus.Failed);
        command.FailureReason.Should().Be("RCON port is not configured.");
        executor.CallCount.Should().Be(0);
        repository.CompleteCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchNextAsync_reports_lost_completion_without_recording_completed_metric()
    {
        var command = CreateCommand(ServerCommandType.Say, "hello");
        var repository = CreateRepository(command, completeSuccessfully: false);
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded());
        var clock = new SequenceClock(
            new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 25, 12, 1, 2, TimeSpan.Zero));
        var sut = new CommandDispatcher(repository, executor, clock);
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Kind.Should().Be(CommandDispatchAttemptResultKind.CompletionLost);
        metrics.Measurements.Should().NotContain(metric =>
            metric.Name == "goldsrcops.commands.completed");
    }

    [Fact]
    public async Task DispatchNextAsync_returns_no_command_when_nothing_can_be_claimed()
    {
        var repository = new InMemoryCommandExecutionRepository(dispatchContext: null);
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded());
        var sut = new CommandDispatcher(
            repository,
            executor,
            new SequenceClock(new DateTimeOffset(2026, 4, 25, 12, 1, 0, TimeSpan.Zero)));

        var result = await sut.DispatchNextAsync(CancellationToken.None);

        result.Kind.Should().Be(CommandDispatchAttemptResultKind.NoCommand);
        result.CommandId.Should().BeNull();
        executor.CallCount.Should().Be(0);
        repository.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task RecoverInterruptedAsync_uses_cutoff_and_records_recovered_metric()
    {
        var now = new DateTimeOffset(2026, 4, 25, 12, 5, 0, TimeSpan.Zero);
        var repository = new InMemoryCommandExecutionRepository(dispatchContext: null)
        {
            InterruptedCount = 2
        };
        var executor = new CapturingRconCommandExecutor(RconCommandExecutionResult.Succeeded());
        var sut = new CommandDispatcher(repository, executor, new SequenceClock(now));
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var result = await sut.RecoverInterruptedAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        result.Should().Be(2);
        repository.LastRecovery.Should().BeEquivalentTo(new
        {
            StartedBeforeUtc = now - TimeSpan.FromSeconds(30),
            CompletedAtUtc = now,
            FailureReason = CommandDispatcher.InterruptedFailureReason
        });
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.commands.recovered" && metric.Value == 2);
    }

    private static InMemoryCommandExecutionRepository CreateRepository(
        CommandExecution command,
        int? rconPort = 27015,
        bool completeSuccessfully = true) =>
        new(
            new CommandExecutionDispatchContext(
                command,
                Host: "127.0.0.1",
                RconPort: rconPort,
                CredentialSecretReference: "rcon-secret://server_rcon"),
            completeSuccessfully);

    private static CommandExecution CreateCommand(ServerCommandType type, string? payload)
    {
        return new CommandExecution(
            Guid.NewGuid(),
            type,
            payload,
            requestedBy: "admin",
            new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero));
    }

    private static bool HasTag(CollectedMetric measurement, string key, string value) =>
        measurement.Tags.Any(tag =>
            string.Equals(tag.Key, key, StringComparison.Ordinal) &&
            string.Equals(tag.Value as string, value, StringComparison.Ordinal));

    private sealed class InMemoryCommandExecutionRepository : ICommandExecutionRepository
    {
        private readonly bool _completeSuccessfully;
        private CommandExecutionDispatchContext? _dispatchContext;

        public InMemoryCommandExecutionRepository(
            CommandExecutionDispatchContext? dispatchContext,
            bool completeSuccessfully = true)
        {
            _dispatchContext = dispatchContext;
            _completeSuccessfully = completeSuccessfully;
        }

        public int CompleteCount { get; private set; }

        public int InterruptedCount { get; init; }

        public RecoveryCall? LastRecovery { get; private set; }

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

        public Task<CommandExecutionDispatchContext?> ClaimNextPendingAsync(
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var context = _dispatchContext;
            _dispatchContext = null;
            context?.Command.MarkRunning(startedAtUtc);
            return Task.FromResult(context);
        }

        public Task<bool> CompleteClaimedAsync(
            CommandExecution command,
            DateTimeOffset claimedAtUtc,
            CancellationToken cancellationToken)
        {
            CompleteCount++;
            command.StartedAtUtc.Should().Be(claimedAtUtc);
            return Task.FromResult(_completeSuccessfully);
        }

        public Task<int> FailInterruptedAsync(
            DateTimeOffset startedBeforeUtc,
            DateTimeOffset completedAtUtc,
            string failureReason,
            CancellationToken cancellationToken)
        {
            LastRecovery = new RecoveryCall(startedBeforeUtc, completedAtUtc, failureReason);
            return Task.FromResult(InterruptedCount);
        }

        public Task<IReadOnlyList<CommandExecution>> ListByServerAsync(
            Guid serverId,
            int limit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record RecoveryCall(
        DateTimeOffset StartedBeforeUtc,
        DateTimeOffset CompletedAtUtc,
        string FailureReason);

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
