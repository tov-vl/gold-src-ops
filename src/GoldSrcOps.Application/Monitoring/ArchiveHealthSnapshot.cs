namespace GoldSrcOps.Application.Monitoring;

public sealed record ArchiveHealthSnapshot(IReadOnlyList<ArchiveMetricSample>? Metrics, IReadOnlyList<ArchiveRuleSample>? Rules);
