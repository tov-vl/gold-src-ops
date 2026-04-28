using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.UnitTests.Helpers;

internal static class TestData
{
    public static DashboardServerStatusDto DashboardServerStatus(
        ServerStatus status,
        bool isEnabled = true,
        DateTimeOffset? lastCheckedAtUtc = null)
    {
        return new DashboardServerStatusDto(Guid.NewGuid(), isEnabled, status, lastCheckedAtUtc);
    }

    public static PollSnapshotDto PollSnapshot(
        Guid serverId,
        DateTimeOffset checkedAtUtc,
        bool isReachable = true)
    {
        return new PollSnapshotDto(
            Guid.NewGuid(),
            serverId,
            checkedAtUtc,
            isReachable,
            LatencyMs: isReachable ? 42 : null,
            Map: isReachable ? "de_dust2" : null,
            Players: isReachable ? 10 : null,
            MaxPlayers: isReachable ? 32 : null,
            Bots: isReachable ? 0 : null,
            RawVersion: isReachable ? "1.1.2.7/Stdio" : null,
            FailureReason: isReachable ? null : "timeout");
    }
}
