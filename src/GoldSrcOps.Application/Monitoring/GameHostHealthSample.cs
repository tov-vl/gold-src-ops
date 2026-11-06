namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostHealthSample(int Panel, string Role, double Value, DateTimeOffset SampledAtUtc);
