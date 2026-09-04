namespace GoldSrcOps.AvailabilityExporter;

internal interface IProbeFailureDetailSource
{
    TimeSpan CorrelationTolerance { get; }

    Task<IReadOnlyList<ProbeFailureDetail>> QueryAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken);
}
