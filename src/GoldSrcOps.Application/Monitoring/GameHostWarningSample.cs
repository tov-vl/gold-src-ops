namespace GoldSrcOps.Application.Monitoring;

public sealed record GameHostWarningSample(string Group, string Name, string Component, string Severity,
    string State, string Health, DateTimeOffset LastEvaluationUtc);
