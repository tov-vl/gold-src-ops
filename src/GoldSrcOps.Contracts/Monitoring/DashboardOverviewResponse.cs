namespace GoldSrcOps.Contracts.Monitoring;

public sealed record DashboardOverviewResponse(
    int TotalServers,
    int EnabledServers,
    int DisabledServers,
    int OnlineServers,
    int OfflineServers,
    int UnknownServers,
    int OpenIncidents,
    DateTimeOffset? LastCheckedAtUtc);
