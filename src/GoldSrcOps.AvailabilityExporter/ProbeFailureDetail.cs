using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed record ProbeFailureDetail(
    DateTimeOffset ObservedAtUtc,
    ProbeFailureKind FailureKind);
