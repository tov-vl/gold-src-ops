namespace GoldSrcOps.Application.Monitoring;

public sealed record DashboardOverviewDto(
    int TotalServers,
    int EnabledServers,
    int DisabledServers,
    int OnlineServers,
    int OfflineServers,
    int UnknownServers,
    int OpenIncidents,
    DateTimeOffset? LastCheckedAtUtc);
