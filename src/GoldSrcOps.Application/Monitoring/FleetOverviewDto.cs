namespace GoldSrcOps.Application.Monitoring;

public sealed record FleetOverviewDto(
    DashboardOverviewDto Overview,
    IReadOnlyList<FleetServerSummaryDto> Servers);
