namespace GoldSrcOps.Contracts.Monitoring;

public sealed record GameHostWarningsResponse(string State, DateTimeOffset? ObservedAtUtc, IReadOnlyList<GameHostWarningResponse> Items);
