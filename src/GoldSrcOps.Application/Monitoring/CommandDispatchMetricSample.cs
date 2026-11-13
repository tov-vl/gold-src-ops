namespace GoldSrcOps.Application.Monitoring;

public sealed record CommandDispatchMetricSample(string Source, string Metric, double Value, DateTimeOffset SampledAtUtc);
