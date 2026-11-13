namespace GoldSrcOps.Application.Monitoring;

public sealed record CommandDispatchHealthSnapshot(IReadOnlyList<CommandDispatchMetricSample>? Metrics, IReadOnlyList<CommandDispatchRuleSample>? Rules);
