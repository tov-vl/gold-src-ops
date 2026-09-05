namespace GoldSrcOps.Contracts.Monitoring;

public sealed record PublicStatusResponse(
    string State,
    int MonitoredServers,
    int OnlineServers,
    int ServersRequiringAttention,
    int OpenIncidents,
    DateTimeOffset? LastObservedAtUtc);
