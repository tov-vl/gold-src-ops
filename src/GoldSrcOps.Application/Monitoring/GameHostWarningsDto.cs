namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostWarningsDto(string State, DateTimeOffset? ObservedAtUtc, IReadOnlyList<GameHostWarningDto> Items);
