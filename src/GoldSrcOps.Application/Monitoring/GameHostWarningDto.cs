namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostWarningDto(string Name, string Component, string State, string Description);
