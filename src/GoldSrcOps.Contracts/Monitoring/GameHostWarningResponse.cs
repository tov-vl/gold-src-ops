namespace GoldSrcOps.Contracts.Monitoring;

public sealed record GameHostWarningResponse(string Name, string Component, string State, string Description);
