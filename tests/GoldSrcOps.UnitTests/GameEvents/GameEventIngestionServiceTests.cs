using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Application.Telemetry;
using GoldSrcOps.Domain.GameEvents;
using GoldSrcOps.UnitTests.Helpers;

namespace GoldSrcOps.UnitTests.GameEvents;

public sealed class GameEventIngestionServiceTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IngestAsync_accepts_once_and_returns_original_receipt_for_idempotent_replay()
    {
        var serverId = Guid.NewGuid();
        var repository = new InMemoryGameEventInboxRepository(serverId);
        var sut = new GameEventIngestionService(repository, new TestClock(NowUtc));
        var command = CreateCommand();
        var equivalentReplay = command with
        {
            OccurredAtUtc = command.OccurredAtUtc.ToOffset(TimeSpan.FromHours(3)),
            Map = " de_dust2 "
        };
        using var metrics = new MetricsCollector(GoldSrcOpsMetrics.MeterName);

        var accepted = await sut.IngestAsync(serverId, command, CancellationToken.None);
        var replayed = await sut.IngestAsync(serverId, equivalentReplay, CancellationToken.None);

        accepted.Kind.Should().Be(GameEventIngestResultKind.Accepted);
        accepted.Event.Should().NotBeNull();
        accepted.Event!.Duplicate.Should().BeFalse();
        accepted.Event.ReceivedAtUtc.Should().Be(NowUtc);
        replayed.Kind.Should().Be(GameEventIngestResultKind.Idempotent);
        replayed.Event.Should().Be(accepted.Event with { Duplicate = true });
        repository.Entries.Should().ContainSingle();
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.game_events.ingestion_requests" &&
            metric.Value == 1 &&
            HasTag(metric, "event_type", "round_started") &&
            HasTag(metric, "result", "accepted"));
        metrics.Measurements.Should().Contain(metric =>
            metric.Name == "goldsrcops.game_events.ingestion_requests" &&
            metric.Value == 1 &&
            HasTag(metric, "result", "idempotent"));
    }

    [Fact]
    public async Task IngestAsync_rejects_event_id_reuse_for_different_content()
    {
        var serverId = Guid.NewGuid();
        var repository = new InMemoryGameEventInboxRepository(serverId);
        var sut = new GameEventIngestionService(repository, new TestClock(NowUtc));
        var command = CreateCommand();
        await sut.IngestAsync(serverId, command, CancellationToken.None);

        var result = await sut.IngestAsync(
            serverId,
            command with { Players = 11 },
            CancellationToken.None);

        result.Kind.Should().Be(GameEventIngestResultKind.EventIdConflict);
        result.Event.Should().BeNull();
        repository.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task IngestAsync_rejects_source_sequence_reuse_for_a_different_event()
    {
        var serverId = Guid.NewGuid();
        var repository = new InMemoryGameEventInboxRepository(serverId);
        var sut = new GameEventIngestionService(repository, new TestClock(NowUtc));
        var command = CreateCommand();
        await sut.IngestAsync(serverId, command, CancellationToken.None);

        var result = await sut.IngestAsync(
            serverId,
            command with { EventId = Guid.NewGuid() },
            CancellationToken.None);

        result.Kind.Should().Be(GameEventIngestResultKind.SourceSequenceConflict);
        result.Event.Should().BeNull();
        repository.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task IngestAsync_rejects_unknown_server_without_writing()
    {
        var repository = new InMemoryGameEventInboxRepository(Guid.NewGuid());
        var sut = new GameEventIngestionService(repository, new TestClock(NowUtc));

        var result = await sut.IngestAsync(
            Guid.NewGuid(),
            CreateCommand(),
            CancellationToken.None);

        result.Kind.Should().Be(GameEventIngestResultKind.ServerNotFound);
        repository.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task IngestAsync_rejects_event_beyond_the_future_clock_skew_without_writing()
    {
        var serverId = Guid.NewGuid();
        var repository = new InMemoryGameEventInboxRepository(serverId);
        var sut = new GameEventIngestionService(repository, new TestClock(NowUtc));
        var command = CreateCommand() with
        {
            OccurredAtUtc = NowUtc + GameEventIngestionService.MaximumFutureClockSkew +
                TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond)
        };

        var result = await sut.IngestAsync(serverId, command, CancellationToken.None);

        result.Kind.Should().Be(GameEventIngestResultKind.OccurredAtInFuture);
        repository.ServerExistenceChecks.Should().Be(0);
        repository.Entries.Should().BeEmpty();
    }

    private static IngestGameEventCommand CreateCommand() =>
        new(
            GameEventInboxEntry.CurrentContractVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            SequenceNumber: 1,
            GameEventType.RoundStarted,
            NowUtc.AddSeconds(-1),
            Map: "de_dust2",
            Players: 10,
            Bots: 0);

    private static bool HasTag(CollectedMetric metric, string key, object? expected) =>
        metric.Tags.TryGetValue(key, out var actual) && Equals(actual, expected);

    private sealed class InMemoryGameEventInboxRepository(Guid existingServerId)
        : IGameEventInboxRepository
    {
        public List<GameEventInboxEntry> Entries { get; } = [];

        public int ServerExistenceChecks { get; private set; }

        public Task<bool> ServerExistsAsync(Guid serverId, CancellationToken cancellationToken)
        {
            ServerExistenceChecks++;
            return Task.FromResult(serverId == existingServerId);
        }

        public Task<GameEventInboxPersistenceResult> StoreAsync(
            GameEventInboxEntry entry,
            CancellationToken cancellationToken)
        {
            var existing = Entries.SingleOrDefault(candidate => candidate.Id == entry.Id);
            if (existing is not null)
            {
                return Task.FromResult(new GameEventInboxPersistenceResult(
                    GameEventInboxPersistenceResultKind.EventIdExists,
                    existing));
            }

            existing = Entries.SingleOrDefault(candidate =>
                candidate.ServerId == entry.ServerId &&
                candidate.SourceInstanceId == entry.SourceInstanceId &&
                candidate.SequenceNumber == entry.SequenceNumber);
            if (existing is not null)
            {
                return Task.FromResult(new GameEventInboxPersistenceResult(
                    GameEventInboxPersistenceResultKind.SourceSequenceExists,
                    existing));
            }

            Entries.Add(entry);
            return Task.FromResult(new GameEventInboxPersistenceResult(
                GameEventInboxPersistenceResultKind.Created,
                entry));
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
