namespace GoldSrcOps.Application.Monitoring;

public sealed record RetentionMetricSample(string Source, string Metric, double Value, DateTimeOffset SampledAtUtc);
