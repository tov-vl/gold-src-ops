namespace GoldSrcOps.Application.Monitoring;

public sealed record PollingHealthSnapshot(IReadOnlyList<PollingMetricSample>? Metrics, IReadOnlyList<PollingRuleSample>? Rules);
