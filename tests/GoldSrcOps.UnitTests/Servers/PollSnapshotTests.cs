using AwesomeAssertions;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class PollSnapshotTests
{
    [Fact]
    public void Reachable_bounds_external_text_to_persistence_limits()
    {
        var map = new string('m', PollSnapshot.MaxMapLength + 1);
        var rawVersion = new string('v', PollSnapshot.MaxRawVersionLength + 1);

        var snapshot = PollSnapshot.Reachable(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            latencyMs: 25,
            map: map,
            players: 1,
            maxPlayers: 32,
            bots: 0,
            rawVersion: rawVersion);

        snapshot.Map.Should().Be(map[..PollSnapshot.MaxMapLength]);
        snapshot.RawVersion.Should().Be(rawVersion[..PollSnapshot.MaxRawVersionLength]);
    }

    [Fact]
    public void Unreachable_bounds_failure_reason_to_persistence_limit()
    {
        var failureReason = new string('f', PollSnapshot.MaxFailureReasonLength + 1);

        var snapshot = PollSnapshot.Unreachable(Guid.NewGuid(), DateTimeOffset.UtcNow, failureReason);

        snapshot.FailureReason.Should().Be(failureReason[..PollSnapshot.MaxFailureReasonLength]);
    }
}
