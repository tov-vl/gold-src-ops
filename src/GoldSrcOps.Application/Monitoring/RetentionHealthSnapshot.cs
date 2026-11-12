namespace GoldSrcOps.Application.Monitoring;

public sealed record RetentionHealthSnapshot(IReadOnlyList<RetentionMetricSample>? Metrics, IReadOnlyList<RetentionRuleSample>? Rules);
