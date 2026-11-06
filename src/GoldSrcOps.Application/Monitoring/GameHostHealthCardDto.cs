namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostHealthCardDto(string Component, string State, DateTimeOffset? ObservedAtUtc);
