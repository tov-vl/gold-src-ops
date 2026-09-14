using AwesomeAssertions;
using GoldSrcOps.Domain.GameEvents;

namespace GoldSrcOps.UnitTests.GameEvents;

public sealed class GameEventInboxEntryTests
{
    private static readonly string IntentHash = new('A', GameEventInboxEntry.MaxIntentHashLength);

    [Fact]
    public void Constructor_normalizes_bounded_event_data()
    {
        var eventId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var sourceInstanceId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));
        var receivedAt = occurredAt.AddSeconds(1);

        var entry = new GameEventInboxEntry(
            eventId,
            serverId,
            sourceInstanceId,
            sequenceNumber: 42,
            GameEventInboxEntry.CurrentContractVersion,
            GameEventType.RoundEnded,
            occurredAt,
            receivedAt,
            map: " de_dust2 ",
            players: 12,
            bots: 0,
            IntentHash.ToLowerInvariant());

        entry.Id.Should().Be(eventId);
        entry.ServerId.Should().Be(serverId);
        entry.SourceInstanceId.Should().Be(sourceInstanceId);
        entry.SequenceNumber.Should().Be(42);
        entry.ContractVersion.Should().Be(GameEventInboxEntry.CurrentContractVersion);
        entry.Type.Should().Be(GameEventType.RoundEnded);
        entry.OccurredAtUtc.Should().Be(occurredAt.ToUniversalTime());
        entry.ReceivedAtUtc.Should().Be(receivedAt.ToUniversalTime());
        entry.Map.Should().Be("de_dust2");
        entry.Players.Should().Be(12);
        entry.Bots.Should().Be(0);
        entry.IntentHash.Should().Be(IntentHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../de_dust2")]
    [InlineData("de dust2")]
    public void Constructor_rejects_missing_or_unsafe_map_for_round_events(string? map)
    {
        var act = () => CreateEntry(map: map);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_allows_server_lifecycle_event_without_map_or_population()
    {
        var entry = CreateEntry(
            type: GameEventType.ServerStarted,
            map: null,
            players: null,
            bots: null);

        entry.Map.Should().BeNull();
        entry.Players.Should().BeNull();
        entry.Bots.Should().BeNull();
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(null, 0)]
    [InlineData(-1, 0)]
    [InlineData(256, 0)]
    [InlineData(5, -1)]
    [InlineData(5, 6)]
    public void Constructor_rejects_invalid_population(int? players, int? bots)
    {
        var act = () => CreateEntry(players: players, bots: bots);

        act.Should().Throw<ArgumentException>();
    }

    private static GameEventInboxEntry CreateEntry(
        GameEventType type = GameEventType.RoundStarted,
        string? map = "de_dust2",
        int? players = 10,
        int? bots = 0) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            sequenceNumber: 1,
            GameEventInboxEntry.CurrentContractVersion,
            type,
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 1, TimeSpan.Zero),
            map,
            players,
            bots,
            IntentHash);
}
