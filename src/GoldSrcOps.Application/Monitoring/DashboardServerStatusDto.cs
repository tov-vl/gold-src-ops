using GoldSrcOps.Domain.Servers;

namespace GoldSrcOps.Application.Monitoring;

public sealed record DashboardServerStatusDto(
    Guid ServerId,
    bool IsEnabled,
    ServerStatus Status,
    DateTimeOffset? LastCheckedAtUtc);
