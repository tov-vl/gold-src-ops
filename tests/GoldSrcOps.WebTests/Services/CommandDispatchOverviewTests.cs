using AwesomeAssertions;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class CommandDispatchOverviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_dispatch_failure_preserves_all_seven_existing_sources(bool unavailable)
    {
        var fixture = new OperatorHealthOverviewTests.FixtureClient();
        var original = await OperatorHealthOverview.WithRetentionAsync(OperatorHealthOverview.ReadAsync(fixture, fixture, fixture, fixture, fixture,
            TimeProvider.System, CancellationToken.None), new RetentionHealthFixture(), TimeProvider.System, CancellationToken.None);
        var client = new CommandDispatchHealthFixture { Unavailable = unavailable };
        var result = await OperatorHealthOverview.WithCommandDispatchAsync(Task.FromResult(original), client, TimeProvider.System, CancellationToken.None);
        result.Sources.Take(7).Should().Equal(original.Sources);
        result.Sources[^1].Key.Should().Be("command-dispatch");
        result.Sources[^1].SnapshotState.Should().Be(unavailable ? "Unknown" : "Available");
        result.Sources[^1].Facts[0].State.Should().Be(unavailable ? "Unknown" : "Running");
        original.Sources.Should().HaveCount(7);
        result.Sources.Should().HaveCount(8);
        client.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_is_not_converted_to_unknown()
    {
        var original = OperatorHealthOverview.Create(gameHost: null, archive: null, polling: null, credentials: null, DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = cancellation.Token;
        var action = () => OperatorHealthOverview.WithCommandDispatchAsync(Task.FromResult(original), new CommandDispatchHealthFixture(), TimeProvider.System, token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }
}
