namespace GoldSrcOps.Contracts.Monitoring;

public sealed record GameHostHealthValueResponse(string Metric, string Role, double Value);
