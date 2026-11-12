namespace GoldSrcOps.Application.Monitoring;

public sealed record PollingMetricSample(string Source, string Metric, double Value, DateTimeOffset SampledAtUtc);
