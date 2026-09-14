using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class GameEventDispatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_removes_acknowledged_event(bool idempotent)
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);
        var result = idempotent
            ? GameEventDeliveryResult.Idempotent
            : GameEventDeliveryResult.Accepted;
        var delivery = new StubDeliveryClient(result);
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var options = GameEventAgentTestData.CreateDeliveryOptions();
        var sut = CreateDispatcher(outbox, delivery, options, time);

        var summary = await sut.DispatchAvailableAsync(CancellationToken.None);

        summary.Acknowledged.Should().Be(1);
        outbox.GetStatistics().Should().Be(new GameEventQueueStatistics(0, 0, 0, 2));
    }

    [Fact]
    public async Task Dispatch_reschedules_retryable_failure_with_exponential_delay()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);
        var delivery = new StubDeliveryClient(
            GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.Network));
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var options = GameEventAgentTestData.CreateDeliveryOptions(
            retryBaseDelay: TimeSpan.FromSeconds(5));
        var sut = CreateDispatcher(outbox, delivery, options, time);

        var summary = await sut.DispatchAvailableAsync(CancellationToken.None);
        var beforeDelay = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(4),
            options.LeaseDuration);
        var afterDelay = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(5),
            options.LeaseDuration);

        summary.Retried.Should().Be(1);
        beforeDelay.Should().BeNull();
        afterDelay.Should().NotBeNull();
        afterDelay!.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task Dispatch_dead_letters_conflict_without_retry()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);
        var delivery = new StubDeliveryClient(
            GameEventDeliveryResult.Permanent(GameEventDeliveryFailure.Conflict));
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var options = GameEventAgentTestData.CreateDeliveryOptions();
        var sut = CreateDispatcher(outbox, delivery, options, time);

        var summary = await sut.DispatchAvailableAsync(CancellationToken.None);

        summary.DeadLettered.Should().Be(1);
        outbox.GetStatistics().DeadLetter.Should().Be(1);
        delivery.SentEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Dispatch_dead_letters_retryable_failure_at_attempt_limit()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);
        var delivery = new StubDeliveryClient(
            GameEventDeliveryResult.Retryable(GameEventDeliveryFailure.RemoteServer));
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var options = GameEventAgentTestData.CreateDeliveryOptions(maximumAttempts: 1);
        var sut = CreateDispatcher(outbox, delivery, options, time);

        var summary = await sut.DispatchAvailableAsync(CancellationToken.None);

        summary.DeadLettered.Should().Be(1);
        outbox.GetStatistics().DeadLetter.Should().Be(1);
    }

    [Fact]
    public async Task Dispatch_expires_old_event_without_network_delivery()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var oldTime = GameEventAgentTestData.NowUtc - TimeSpan.FromDays(31);
        outbox.Enqueue(
            GameEventAgentTestData.CreateInput() with { OccurredAtUtc = oldTime },
            oldTime);
        var delivery = new StubDeliveryClient();
        var time = new TestTimeProvider(GameEventAgentTestData.NowUtc);
        var options = GameEventAgentTestData.CreateDeliveryOptions(
            maximumEventAge: TimeSpan.FromDays(30));
        var sut = CreateDispatcher(outbox, delivery, options, time);

        var summary = await sut.DispatchAvailableAsync(CancellationToken.None);

        summary.DeadLettered.Should().Be(1);
        delivery.SentEvents.Should().BeEmpty();
        outbox.GetStatistics().DeadLetter.Should().Be(1);
    }

    private static GameEventDispatcher CreateDispatcher(
        IGameEventOutbox outbox,
        IGameEventDeliveryClient deliveryClient,
        GameEventDeliveryOptions options,
        TimeProvider timeProvider) =>
        new(
            outbox,
            deliveryClient,
            new ExponentialRetryDelayPolicy(options),
            options,
            timeProvider);
}
