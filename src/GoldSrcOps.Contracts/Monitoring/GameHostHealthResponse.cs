namespace GoldSrcOps.Contracts.Monitoring;

public sealed record GameHostHealthResponse(DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<GameHostHealthComponentResponse> Components, IReadOnlyList<GameHostHealthValueResponse> Values, GameHostWarningsResponse? Warnings = null);
