namespace GoldSrcOps.AvailabilityExporter;

internal sealed record PrometheusSample(DateTimeOffset EvaluatedAtUtc, double Value);
