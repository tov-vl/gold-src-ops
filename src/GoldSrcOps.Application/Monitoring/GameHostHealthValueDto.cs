namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostHealthValueDto(string Metric, string Role, double Value);
