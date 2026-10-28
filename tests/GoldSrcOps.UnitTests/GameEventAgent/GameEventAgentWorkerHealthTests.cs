using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventAgentWorkerHealthTests
{
    [Fact]
    public async Task Idle_import_loop_reports_progress_without_rounds_and_stops_cleanly()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        var outbox = new SqliteGameEventOutbox(options.Queue);
        outbox.Initialize();
        var clock = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var health = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        using var worker = new GameEventSpoolWorker(new GameEventSpoolImporter(options.Spool, outbox, clock),
            options.Spool, clock, health, NullLogger<GameEventSpoolWorker>.Instance);

        await RunOnePassAsync(worker, () => GameEventAgentHealth.Read(options, clock.UtcNow).Import);

        var result = GameEventAgentStatus.Capture(options, clock);
        result.Queue.Pending.Should().Be(0);
        result.Health.Import.State.Should().Be("stopped");
        result.Health.Import.Cycle!.Outcome.Should().Be("idle");
        result.Health.Import.Cycle.LastSuccessfulAtUtc.Should().Be(clock.UtcNow);
    }

    [Fact]
    public async Task Failed_delivery_preserves_retry_and_queue_and_successful_pass_recovers()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var delivery = GameEventAgentTestData.CreateDeliveryOptions(batchSize: 1);
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(), delivery);
        var outbox = new SqliteGameEventOutbox(options.Queue);
        outbox.Initialize();
        var queued = outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);
        var original = outbox.GetState();
        var clock = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var client = new StubDeliveryClient(GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Network),
            GameEventDeliveryResult.Accepted);
        var dispatcher = new GameEventDispatcher(outbox, client, new ExponentialRetryDelayPolicy(delivery), delivery, clock);
        var health = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        using (var first = new GameEventDeliveryWorker(outbox, dispatcher, delivery,
            NullLogger<GameEventDeliveryWorker>.Instance, health))
        {
            await RunOnePassAsync(first, () => GameEventAgentHealth.Read(options, clock.UtcNow).Delivery,
                "retrying");
        }

        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetState().Should().Be(original);
        client.SentEvents.Single().EventId.Should().Be(queued.EventId);
        clock.UtcNow += TimeSpan.FromMinutes(1);
        using (var second = new GameEventDeliveryWorker(outbox, dispatcher, delivery,
            NullLogger<GameEventDeliveryWorker>.Instance, health))
        {
            await RunOnePassAsync(second, () => GameEventAgentHealth.Read(options, clock.UtcNow).Delivery,
                "progress");
        }

        outbox.GetStatistics().Pending.Should().Be(0);
        outbox.GetState().Should().Be(original);
        client.SentEvents.Should().HaveCount(2);
        client.SentEvents[1].PayloadUtf8.Should().Equal(client.SentEvents[0].PayloadUtf8);
    }

    [Fact]
    public async Task Import_exception_remains_a_worker_failure_with_diagnostic_evidence()
    {
        using var database = new TemporaryAgentDatabase();
        using var spool = new TemporaryAgentSpool();
        var options = new GameEventAgentOptions(database.CreateOptions(), spool.CreateOptions(enabled: true), Delivery: null);
        var outbox = new SqliteGameEventOutbox(options.Queue);
        outbox.Initialize();
        Directory.CreateDirectory(options.Spool.RootPath);
        await File.WriteAllTextAsync(options.Spool.IncomingPath, "directory-conflict");
        var clock = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var health = new GameEventAgentHealth(options, clock, NullLogger<GameEventAgentHealth>.Instance);
        using var worker = new GameEventSpoolWorker(new GameEventSpoolImporter(options.Spool, outbox, clock),
            options.Spool, clock, health, NullLogger<GameEventSpoolWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        var execution = worker.ExecuteTask!;
        var execute = () => execution.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System);

        await execute.Should().ThrowAsync<IOException>();
        var result = GameEventAgentHealth.Read(options, clock.UtcNow);
        result.Import.State.Should().Be("stopped");
        result.Import.Cycle!.Outcome.Should().Be("failed");
        outbox.GetStatistics().Pending.Should().Be(0);
    }

    private static async Task RunOnePassAsync(
        BackgroundService worker, Func<GameEventAgentCycleStatus> read, string outcome = "idle")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            while (read().Cycle?.LastCompletedAtUtc is null || read().Cycle?.StoppedAtUtc is not null ||
                !string.Equals(read().Cycle?.Outcome, outcome, StringComparison.Ordinal))
            {
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
            }

            read().Cycle!.Outcome.Should().Be(outcome);
            read().State.Should().Be(outcome is "idle" or "progress" ? "running" : outcome);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
