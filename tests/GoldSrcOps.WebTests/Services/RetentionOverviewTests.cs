using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class RetentionOverviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retention_failure_does_not_hide_existing_sources_or_change_the_six_source_report(bool unavailable)
    {
        var now = DateTimeOffset.UtcNow;
        var original = OperatorHealthOverview.Create(OperatorHealthOverviewFixtures.GameHost(now), OperatorHealthOverviewFixtures.Archive(now),
            OperatorHealthOverviewFixtures.Polling(now), OperatorHealthOverviewFixtures.Credentials(now), now);
        var client = new RetentionHealthFixture { Unavailable = unavailable };
        var result = await OperatorHealthOverview.WithRetentionAsync(Task.FromResult(original), client, TimeProvider.System, CancellationToken.None);
        result.Sources.Take(4).Should().Equal(original.Sources);
        result.Sources[^1].Key.Should().Be("retention");
        result.Sources[^1].SnapshotState.Should().Be(unavailable ? "Unknown" : "Available");
        result.Sources[^1].Facts[0].State.Should().Be(unavailable ? "Unknown" : "Running");
        original.Sources.Should().HaveCount(4);
        client.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_unknown()
    {
        var original = OperatorHealthOverview.Create(gameHost: null, archive: null, polling: null, credentials: null, DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = cancellation.Token;
        var action = () => OperatorHealthOverview.WithRetentionAsync(Task.FromResult(original), new RetentionHealthFixture(), TimeProvider.System, token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }
}
