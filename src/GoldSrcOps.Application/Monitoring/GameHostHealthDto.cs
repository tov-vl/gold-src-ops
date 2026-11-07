namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostHealthDto(DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<GameHostHealthCardDto> Components, IReadOnlyList<GameHostHealthValueDto> Values, GameHostWarningsDto? Warnings = null);
