namespace GoldSrcOps.Contracts.Monitoring;

public sealed record FleetOverviewResponse(
    DashboardOverviewResponse Overview,
    IReadOnlyList<FleetServerSummaryResponse> Servers);
