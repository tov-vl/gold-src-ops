namespace GoldSrcOps.Application.Monitoring;

public enum PublicStatusState
{
    Unknown,
    Operational,
    Degraded
}

public sealed record PublicStatusDto(
    PublicStatusState State,
    int MonitoredServers,
    int OnlineServers,
    int ServersRequiringAttention,
    int OpenIncidents,
    DateTimeOffset? LastObservedAtUtc);
