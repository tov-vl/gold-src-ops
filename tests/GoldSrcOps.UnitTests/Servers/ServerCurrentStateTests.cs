using AwesomeAssertions;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Servers;

public sealed class ServerCurrentStateTests
{
    [Fact]
    public void CreateUnknown_initializes_unreachable_unknown_state()
    {
        var serverId = Guid.NewGuid();
        var checkedAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);

        var state = ServerCurrentState.CreateUnknown(serverId, checkedAtUtc);

        Assert.Equal(serverId, state.ServerId);
        Assert.Equal(ServerStatus.Unknown, state.Status);
        Assert.False(state.IsReachable);
        Assert.Equal(checkedAtUtc, state.LastCheckedAtUtc);
        Assert.Null(state.LastSuccessAtUtc);
        Assert.Null(state.LatencyMs);
        Assert.Null(state.CurrentMap);
        Assert.Null(state.Players);
        Assert.Null(state.MaxPlayers);
        Assert.Null(state.FailureReason);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public void MarkOffline_increments_failures_and_preserves_last_success()
    {
        var serverId = Guid.NewGuid();
        var createdAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var successAtUtc = createdAtUtc.AddMinutes(1);
        var failureAtUtc = createdAtUtc.AddMinutes(2);
        var state = ServerCurrentState.CreateUnknown(serverId, createdAtUtc);
        state.MarkOnline(successAtUtc, latencyMs: 25, map: "de_dust2", players: 12, maxPlayers: 32);

        state.MarkOffline(failureAtUtc, "No response.");
        state.MarkOffline(failureAtUtc.AddMinutes(1), "Still no response.");

        Assert.Equal(ServerStatus.Offline, state.Status);
        Assert.False(state.IsReachable);
        Assert.Equal(failureAtUtc.AddMinutes(1), state.LastCheckedAtUtc);
        Assert.Equal(successAtUtc, state.LastSuccessAtUtc);
        Assert.Null(state.LatencyMs);
        Assert.Null(state.CurrentMap);
        Assert.Null(state.Players);
        Assert.Null(state.MaxPlayers);
        Assert.Equal("Still no response.", state.FailureReason);
        Assert.Equal(2, state.ConsecutiveFailures);
    }

    [Fact]
    public void MarkOnline_resets_failure_state()
    {
        var serverId = Guid.NewGuid();
        var createdAtUtc = new DateTimeOffset(2026, 4, 25, 12, 0, 0, TimeSpan.Zero);
        var recoveryAtUtc = createdAtUtc.AddMinutes(3);
        var state = ServerCurrentState.CreateUnknown(serverId, createdAtUtc);
        state.MarkOffline(createdAtUtc.AddMinutes(1), "No response.");

        state.MarkOnline(recoveryAtUtc, latencyMs: 41, map: " de_inferno ", players: 8, maxPlayers: 32);

        Assert.Equal(ServerStatus.Online, state.Status);
        Assert.True(state.IsReachable);
        Assert.Equal(recoveryAtUtc, state.LastCheckedAtUtc);
        Assert.Equal(recoveryAtUtc, state.LastSuccessAtUtc);
        Assert.Equal(41, state.LatencyMs);
        Assert.Equal("de_inferno", state.CurrentMap);
        Assert.Equal(8, state.Players);
        Assert.Equal(32, state.MaxPlayers);
        Assert.Null(state.FailureReason);
        Assert.Equal(0, state.ConsecutiveFailures);
    }

    [Fact]
    public void Monitoring_text_is_bounded_to_persistence_limits()
    {
        var state = ServerCurrentState.CreateUnknown(Guid.NewGuid(), DateTimeOffset.UtcNow);
        var map = new string('m', ServerCurrentState.MaxMapLength + 1);
        var failureReason = new string('f', ServerCurrentState.MaxFailureReasonLength + 1);

        state.MarkOnline(
            DateTimeOffset.UtcNow,
            latencyMs: 25,
            map: map,
            players: 1,
            maxPlayers: 32);

        state.CurrentMap.Should().Be(map[..ServerCurrentState.MaxMapLength]);

        state.MarkOffline(DateTimeOffset.UtcNow, failureReason);

        state.FailureReason.Should().Be(failureReason[..ServerCurrentState.MaxFailureReasonLength]);
    }
}
