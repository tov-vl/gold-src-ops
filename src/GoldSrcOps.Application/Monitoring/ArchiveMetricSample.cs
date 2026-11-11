namespace GoldSrcOps.Application.Monitoring;

public sealed record ArchiveMetricSample(string Metric, double Value, DateTimeOffset SampledAtUtc);
