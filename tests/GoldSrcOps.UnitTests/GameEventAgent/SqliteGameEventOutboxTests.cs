using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.GameEventAgent;

namespace GoldSrcOps.UnitTests.GameEventAgent;

public sealed class SqliteGameEventOutboxTests
{
    [Fact]
    public async Task Initialize_is_safe_for_concurrent_process_equivalent_instances()
    {
        using var database = new TemporaryAgentDatabase();
        using var ready = new Barrier(participantCount: 8);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                var outbox = new SqliteGameEventOutbox(database.CreateOptions());
                ready.SignalAndWait();
                outbox.Initialize();
                return outbox.GetState();
            }))
            .ToArray();

        var states = await Task.WhenAll(tasks);

        states.Select(static state => state.SourceInstanceId).Distinct().Should().ContainSingle();
        states.Should().AllSatisfy(static state => state.NextSequenceNumber.Should().Be(1));
    }

    [Fact]
    public void Enqueue_preserves_source_identity_and_monotonic_sequence_across_reopen()
    {
        using var database = new TemporaryAgentDatabase();
        var firstOutbox = new SqliteGameEventOutbox(database.CreateOptions());
        firstOutbox.Initialize();

        var first = firstOutbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var reopenedOutbox = new SqliteGameEventOutbox(database.CreateOptions());
        reopenedOutbox.Initialize();
        var second = reopenedOutbox.Enqueue(
            GameEventAgentTestData.CreateInput() with
            {
                Type = "server.stopped",
                Map = null,
                Players = null,
                Bots = null
            },
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1));

        second.SourceInstanceId.Should().Be(first.SourceInstanceId);
        first.SequenceNumber.Should().Be(1);
        second.SequenceNumber.Should().Be(2);
        reopenedOutbox.GetState().NextSequenceNumber.Should().Be(3);
        reopenedOutbox.GetStatistics().Pending.Should().Be(2);
    }

    [Fact]
    public void Claim_reuses_identical_payload_after_an_expired_lease()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());
        var queued = outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var firstClaim = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc,
            TimeSpan.FromSeconds(30));
        var prematureClaim = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(29),
            TimeSpan.FromSeconds(30));
        var recoveredClaim = outbox.ClaimNext(
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));

        firstClaim.Should().NotBeNull();
        prematureClaim.Should().BeNull();
        recoveredClaim.Should().NotBeNull();
        recoveredClaim!.EventId.Should().Be(queued.EventId);
        recoveredClaim.SequenceNumber.Should().Be(queued.SequenceNumber);
        recoveredClaim.AttemptCount.Should().Be(2);
        recoveredClaim.PayloadUtf8.Should().Equal(queued.PayloadUtf8);
    }

    [Fact]
    public void Enqueue_is_atomic_when_capacity_is_reached()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions(capacity: 1));
        outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        var act = () => outbox.Enqueue(
            GameEventAgentTestData.CreateInput(),
            GameEventAgentTestData.NowUtc + TimeSpan.FromSeconds(1));

        act.Should().Throw<GameEventQueueFullException>();
        outbox.GetStatistics().Pending.Should().Be(1);
        outbox.GetState().NextSequenceNumber.Should().Be(2);
    }

    [Fact]
    public void Enqueue_stores_a_bounded_versioned_request_with_no_extension_fields()
    {
        using var database = new TemporaryAgentDatabase();
        var outbox = new SqliteGameEventOutbox(database.CreateOptions());

        var queued = outbox.Enqueue(GameEventAgentTestData.CreateInput(), GameEventAgentTestData.NowUtc);

        queued.PayloadUtf8.Should().HaveCountLessThanOrEqualTo(GameEventJson.MaximumRequestBytes);
        using var document = JsonDocument.Parse(queued.PayloadUtf8);
        document.RootElement.GetProperty("contractVersion").GetInt16().Should().Be(1);
        document.RootElement.GetProperty("eventId").GetGuid().Should().Be(queued.EventId);
        document.RootElement.GetProperty("sourceInstanceId").GetGuid().Should().Be(queued.SourceInstanceId);
        document.RootElement.GetProperty("sequenceNumber").GetInt64().Should().Be(1);
        document.RootElement.EnumerateObject().Should().HaveCount(9);
    }
}
