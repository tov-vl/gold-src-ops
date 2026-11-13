using AwesomeAssertions;
using GoldSrcOps.Application.Commands;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.Commands;
using GoldSrcOps.Infrastructure.Commands;
using GoldSrcOps.UnitTests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GoldSrcOps.UnitTests.Commands;

public sealed class CommandDispatchWorkerTests
{
    [Fact]
    public async Task Batch_keeps_configured_concurrency_and_waits_for_every_dispatch()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = Mock.Of<IClock>(value => value.UtcNow == now);
        var options = new CommandDispatcherOptions { MaxConcurrency = 3, LoopDelay = TimeSpan.FromMinutes(1) };
        using var metrics = new CommandDispatchMetrics(options, new GoldSrcRconOptions(), clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var repository = new Mock<ICommandExecutionRepository>();
        repository.Setup(value => value.ClaimNextPendingAsync(now, It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref calls) == options.MaxConcurrency)
                    entered.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
                return null;
            });
        var dispatcher = new CommandDispatcher(repository.Object, Mock.Of<IRconCommandExecutor>(), clock, NullLogger<CommandDispatcher>.Instance);
        await using var provider = new ServiceCollection().AddScoped(_ => dispatcher).BuildServiceProvider();
        using var worker = new CommandDispatchBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), clock, options,
            NullLogger<CommandDispatchBackgroundService>.Instance, metrics);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            collector.CollectObservableMetrics();
            collector.Measurements.Last(value => value.Name.EndsWith("last_finished_timestamp", StringComparison.Ordinal)).Value.Should().Be(0);
            calls.Should().Be(3);
            release.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                collector.CollectObservableMetrics();
                if (collector.Measurements.Last(value => value.Name.EndsWith("last_finished_timestamp", StringComparison.Ordinal)).Value > 0)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        collector.CollectObservableMetrics();
        calls.Should().Be(3);
        collector.Measurements.Last(value => value.Name.EndsWith("last_successful_cycle_timestamp", StringComparison.Ordinal)).Value.Should().Be((now - DateTimeOffset.UnixEpoch).TotalSeconds);
    }

    [Theory]
    [InlineData("empty", 1)]
    [InlineData("failed-command", 1)]
    [InlineData("lost-completion", 1)]
    [InlineData("claim-error", 2)]
    [InlineData("recovery-error", 2)]
    [InlineData("shutdown", 0)]
    public async Task Worker_observes_cycles_without_retrying_commands_or_counting_shutdown(string scenario, int expected)
    {
        var now = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = Mock.Of<IClock>(value => value.UtcNow == now);
        // A populated batch must progress immediately, even with a long empty-queue delay.
        var populated = scenario is "failed-command" or "lost-completion";
        var options = new CommandDispatcherOptions { MaxConcurrency = 1, LoopDelay = populated ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(1) };
        using var metrics = new CommandDispatchMetrics(options, new GoldSrcRconOptions(), clock);
        using var collector = new MetricsCollector(GoldSrcOpsMetrics.MeterName, instrument => ReferenceEquals(instrument.Meter.Scope, options));
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var recoveryCalls = 0;
        var command = new CommandExecution(Guid.NewGuid(), ServerCommandType.Say, "fixture", "fixture", now);
        var repository = new Mock<ICommandExecutionRepository>(MockBehavior.Strict);
        repository.Setup(value => value.FailInterruptedAsync(now - options.InterruptedAfter, now, CommandDispatcher.InterruptedFailureReason, It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset _, DateTimeOffset _, string _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref recoveryCalls) > 1)
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                return string.Equals(scenario, "recovery-error", StringComparison.Ordinal)
                    ? throw new InvalidOperationException("fixture recovery error")
                    : 0;
            });
        repository.Setup(value => value.ClaimNextPendingAsync(now, It.IsAny<CancellationToken>()))
            .Returns(async (DateTimeOffset _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref calls) > 1 || string.Equals(scenario, "shutdown", StringComparison.Ordinal))
                {
                    blocked.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                }
                if (string.Equals(scenario, "claim-error", StringComparison.Ordinal))
                    throw new InvalidOperationException("fixture claim error");
                if (!populated)
                    return null;
                command.MarkRunning(now);
                return new CommandExecutionDispatchContext(command, "127.0.0.1", 27015, "fixture-reference");
            });
        repository.Setup(value => value.CompleteClaimedAsync(command, now, It.IsAny<CancellationToken>())).ReturnsAsync(!string.Equals(scenario, "lost-completion", StringComparison.Ordinal));
        var executor = new Mock<IRconCommandExecutor>(MockBehavior.Strict);
        executor.Setup(value => value.ExecuteAsync(It.IsAny<RconCommandExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RconCommandExecutionResult.Failed("fixture failure"));
        var dispatcher = new CommandDispatcher(repository.Object, executor.Object, clock, NullLogger<CommandDispatcher>.Instance);
        await using var provider = new ServiceCollection().AddScoped(_ => dispatcher).BuildServiceProvider();
        using var worker = new CommandDispatchBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), clock, options,
            NullLogger<CommandDispatchBackgroundService>.Instance, metrics);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            collector.CollectObservableMetrics();
            Read("state").Should().Be(expected);
            Read("in_progress").Should().Be(1);
            Read("consecutive_failures").Should().Be(expected == 2 ? 1 : 0);
            Read("last_successful_cycle_timestamp").Should().Be(expected == 1 ? (now - DateTimeOffset.UnixEpoch).TotalSeconds : 0);
            recoveryCalls.Should().Be(string.Equals(scenario, "recovery-error", StringComparison.Ordinal) ? 2 : 1);
            calls.Should().Be(string.Equals(scenario, "recovery-error", StringComparison.Ordinal) ? 0 : string.Equals(scenario, "shutdown", StringComparison.Ordinal) ? 1 : 2);
            executor.Verify(value => value.ExecuteAsync(It.IsAny<RconCommandExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(populated ? 1 : 0));
            if (populated)
                command.Status.Should().Be(CommandExecutionStatus.Failed);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        collector.CollectObservableMetrics();
        Read("in_progress").Should().Be(0);
        Read("state").Should().Be(0);
        Read("last_failure_timestamp").Should().Be(expected == 2 ? (now - DateTimeOffset.UnixEpoch).TotalSeconds : 0);
        if (string.Equals(scenario, "shutdown", StringComparison.Ordinal))
            Read("last_finished_timestamp").Should().Be(0);

        return;

        double Read(string name) => collector.Measurements.Last(value => string.Equals(value.Name, "goldsrcops.command_dispatch.worker." + name, StringComparison.Ordinal)).Value;
    }
}
