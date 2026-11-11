using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Application.Monitoring;

public sealed class ArchiveHealthService(IArchiveHealthSource source, IClock clock)
{
    private static readonly string[] Metrics = ["observation_success", "observed_timestamp_seconds", "has_success", "last_success_timestamp_seconds"];
    private static readonly string[] Names = ["ArchiveScheduleMonitorUnavailable", "ArchiveScheduleObservationInvalid", "ArchiveScheduleDelayed", "ArchiveScheduleCatchupAtRisk"];

    public async Task<ArchiveHealthDto> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await source.ReadAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var observation = Observe(snapshot.Metrics, now);
        var warnings = Rules(snapshot.Rules, now);
        return new ArchiveHealthDto(now, observation.State, observation.AtUtc, observation.HasSuccess,
            observation.SuccessAtUtc, observation.AgeSeconds, warnings.State, warnings.AtUtc, warnings.Items);
    }

    private static Observation Observe(IReadOnlyList<ArchiveMetricSample>? samples, DateTimeOffset now)
    {
        var unknown = new Observation("Unknown", AtUtc: null, HasSuccess: null, SuccessAtUtc: null, AgeSeconds: null);
        if (samples is null || samples.Count > Metrics.Length || samples.Any(sample =>
            !Metrics.Contains(sample.Metric, StringComparer.Ordinal) || !double.IsFinite(sample.Value) || sample.Value < 0 ||
            sample.SampledAtUtc < now.AddSeconds(-60) || sample.SampledAtUtc > now) ||
            samples.Select(static sample => sample.Metric).Distinct(StringComparer.Ordinal).Count() != samples.Count)
        {
            return unknown;
        }

        var stamp = samples.SingleOrDefault(static sample => sample.Metric is "observed_timestamp_seconds")?.Value;
        var observedAt = Timestamp(stamp, now);
        if (observedAt is null)
        {
            return unknown;
        }

        if (observedAt < now.AddSeconds(-1800))
        {
            return unknown with { State = "Stale", AtUtc = observedAt };
        }

        unknown = unknown with { AtUtc = observedAt };
        if (samples.Count != Metrics.Length || samples.Single(static sample => sample.Metric is "observation_success").Value != 1)
        {
            return unknown;
        }

        var hasSuccess = samples.Single(static sample => sample.Metric is "has_success").Value;
        var last = samples.Single(static sample => sample.Metric is "last_success_timestamp_seconds").Value;
        if (hasSuccess == 0 && last == 0)
        {
            return new Observation("Available", observedAt, HasSuccess: false, SuccessAtUtc: null, AgeSeconds: null);
        }

        var lastSuccessAt = Timestamp(last, now);
        return hasSuccess == 1 && lastSuccessAt is { } successAt && successAt <= observedAt
            ? new Observation("Available", observedAt, HasSuccess: true, successAt, (now - successAt).TotalSeconds)
            : unknown;
    }

    private static DateTimeOffset? Timestamp(double? value, DateTimeOffset now) =>
        value is >= 1577836800 and <= 253402300799 && value <= (now - DateTimeOffset.UnixEpoch).TotalSeconds
            ? DateTimeOffset.UnixEpoch.AddSeconds(value.Value) : null;

    private static (string State, DateTimeOffset? AtUtc, IReadOnlyList<ArchiveWarningDto> Items) Rules(IReadOnlyList<ArchiveRuleSample>? samples, DateTimeOffset now)
    {
        if (samples is null || samples.Count != Names.Length || samples.Select(static sample => sample.Name).Distinct(StringComparer.Ordinal).Count() != Names.Length ||
            samples.Any(sample => !Names.Contains(sample.Name, StringComparer.Ordinal) || sample.Group is not "goldsrcops-archive-schedule" ||
                sample.Component is not "archive-schedule" || sample.Severity is not "warning" || sample.Health is not "ok" ||
                sample.State is not ("inactive" or "pending" or "firing") || sample.EvaluatedAtUtc > now))
        {
            return ("Unknown", null, []);
        }

        var evaluatedAt = samples.Min(static sample => sample.EvaluatedAtUtc);
        return evaluatedAt < now.AddSeconds(-60)
            ? ("Stale", evaluatedAt, [])
            : ("Available", evaluatedAt, [.. samples.Select(static sample => new ArchiveWarningDto(sample.Name, sample.State switch
                {
                    "firing" => "Firing", "pending" => "Pending", _ => "Inactive"
                })).OrderBy(static sample => sample.State switch { "Firing" => 0, "Pending" => 1, _ => 2 }).ThenBy(static sample => sample.Name, StringComparer.Ordinal)]);
    }

    private sealed record Observation(string State, DateTimeOffset? AtUtc, bool? HasSuccess, DateTimeOffset? SuccessAtUtc, double? AgeSeconds);
}
