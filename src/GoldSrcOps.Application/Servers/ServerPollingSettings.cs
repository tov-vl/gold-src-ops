namespace GoldSrcOps.Application.Servers;

public sealed record ServerPollingSettings(
    TimeSpan QueryTimeout,
    int BatchSize,
    int IncidentFailureThreshold);
