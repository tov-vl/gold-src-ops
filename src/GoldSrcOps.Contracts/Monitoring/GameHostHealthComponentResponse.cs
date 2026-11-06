namespace GoldSrcOps.Contracts.Monitoring;

public sealed record GameHostHealthComponentResponse(string Component, string State, DateTimeOffset? ObservedAtUtc);
