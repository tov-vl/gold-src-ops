using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Alerts;
using GoldSrcOps.Contracts.ProviderDelivery;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class IncidentDeliveryHealthViewTests
{
    private static DateTimeOffset Now => OperatorHealthOverviewFixtures.Now;

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    [InlineData(1, "Unknown")]
    public void Each_queue_uses_its_own_observation_time(int offset, string expected)
    {
        var outbox = IncidentDeliveryHealthView.Outbox(OperatorHealthOverviewFixtures.Outbox(Now) with { ObservedAtUtc = Now.AddSeconds(offset) }, Now);
        var receiver = IncidentDeliveryHealthView.Receiver(OperatorHealthOverviewFixtures.Receiver(Now), true, Now);
        outbox.SnapshotState.Should().Be(expected);
        receiver.SnapshotState.Should().Be("Available");
        outbox.Rules.Should().BeNull();
        receiver.Rules.Should().BeNull();
        if (expected is not "Available")
        {
            outbox.Facts.Should().OnlyContain(fact => fact.Value == expected);
        }
    }

    [Fact]
    public void Disabled_workers_and_catchup_do_not_hide_queued_work_or_imply_delivery()
    {
        var outbox = IncidentDeliveryHealthView.Outbox(OperatorHealthOverviewFixtures.Outbox(Now), Now);
        var receiver = IncidentDeliveryHealthView.Receiver(OperatorHealthOverviewFixtures.Receiver(Now), true, Now);
        outbox.Facts.Should().Contain(new OperatorHealthOverview.Fact("Dispatcher", "Disabled"));
        outbox.Facts.Should().Contain(new OperatorHealthOverview.Fact("Pending", "2", "Backlog"));
        receiver.Facts.Should().Contain(new OperatorHealthOverview.Fact("Receiver mode", "Catch-up / sandbox"));
        receiver.Facts.Should().Contain(new OperatorHealthOverview.Fact("Provider worker", "Disabled"));
        receiver.Facts.Should().Contain(new OperatorHealthOverview.Fact("Unreviewed dead letters", "1", "Backlog"));
        receiver.Note.Should().Contain("worker state is independent").And.Contain("does not prove email receipt");
    }

    [Fact]
    public void Enabled_empty_queues_and_long_max_counts_are_valid_without_aggregating_or_overflow()
    {
        var empty = IncidentDeliveryHealthView.Outbox(new AlertDeliveryStatusResponse(true, 0, 0, 0, null, Now), Now);
        empty.SnapshotState.Should().Be("Available");
        empty.Facts.Should().Contain(new OperatorHealthOverview.Fact("Oldest pending", "None"));
        empty.Note.Should().Contain("does not prove delivery");
        var full = IncidentDeliveryHealthView.Receiver(new ProviderDeliveryStatusResponse(true, "Live", long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue, Now.AddYears(-1), Now), true, Now);
        full.SnapshotState.Should().Be("Available");
        full.Facts.Should().Contain(new OperatorHealthOverview.Fact("Receiver mode", "Live"));
        full.Facts.Should().Contain(new OperatorHealthOverview.Fact("Pending", "9223372036854775807", "Backlog"));
    }

    [Theory]
    [InlineData(-1, 0, 0, false)]
    [InlineData(0, -1, 0, false)]
    [InlineData(0, 0, -1, false)]
    [InlineData(1, 0, 0, false)]
    [InlineData(0, 0, 0, true)]
    public void Contradictory_counts_and_oldest_are_unknown(long pending, long processing, long dead, bool hasOldest)
    {
        var value = new AlertDeliveryStatusResponse(true, pending, processing, dead, hasOldest ? Now.AddMinutes(-1) : null, Now);
        IncidentDeliveryHealthView.Outbox(value, Now).Facts.Should().OnlyContain(static fact => fact.Value == "Unknown");
    }

    [Fact]
    public void Invalid_time_mode_and_review_counts_never_reflect_raw_material()
    {
        var value = OperatorHealthOverviewFixtures.Receiver(Now);
        foreach (var invalid in new[]
        {
            value with { ReceiverMode = "private-sentinel\r\nAuthorization: hidden" },
            value with { UnreviewedDeadLetterCount = -1 }, value with { UnreviewedDeadLetterCount = 3 },
            value with { OldestPendingAtUtc = Now }, value with { OldestPendingAtUtc = DateTimeOffset.MinValue },
            value with { ObservedAtUtc = DateTimeOffset.MinValue }
        })
        {
            var result = IncidentDeliveryHealthView.Receiver(invalid, true, Now);
            result.SnapshotState.Should().Be("Unknown");
            JsonSerializer.Serialize(result).Should().NotContain("private-sentinel").And.NotContain("Authorization").And.NotContain("hidden");
        }
        IncidentDeliveryHealthView.Receiver(null, true, Now).SnapshotState.Should().Be("Unknown");
        IncidentDeliveryHealthView.Outbox(null, Now).SnapshotState.Should().Be("Unknown");
    }

    [Fact]
    public async Task Disabled_receiver_access_is_explicit_and_never_queries_it()
    {
        var client = new OperatorHealthOverviewTests.FixtureClient { IsReceiverConfigured = false };
        var result = await OperatorHealthOverview.ReadAsync(client, client, client, client, client, new OperatorHealthOverviewFixtures.Clock(), CancellationToken.None);
        client.Reads.Should().Equal(1, 1, 1, 1, 1, 0);
        result.Sources[4].SnapshotState.Should().Be("Available");
        var receiver = result.Sources[5];
        receiver.SnapshotState.Should().Be("Disabled");
        receiver.EvaluatedAtUtc.Should().BeNull();
        receiver.Facts.Skip(1).Should().OnlyContain(static fact => fact.Value == "Not queried");
    }
}
