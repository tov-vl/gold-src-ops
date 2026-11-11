namespace GoldSrcOps.Application.Monitoring;

public sealed record ArchiveRuleSample(string Name, string Group, string Component, string Severity,
    string State, string Health, DateTimeOffset EvaluatedAtUtc);
