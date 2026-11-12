using GoldSrcOps.Application.Common;

namespace GoldSrcOps.Application.Monitoring;

public sealed class PollingHealthService(IPollingHealthSource source, IClock clock)
{
    private static readonly string[] Fields = ["enabled", "started_timestamp_seconds", "observed_timestamp_seconds", "cycle_budget_seconds", "last_started_timestamp_seconds", "last_finished_timestamp_seconds", "state"];
    private static readonly string[] Names = ["PollingMonitorUnavailable", "PollingCycleStalled", "PollingCycleFailed"];

    public async Task<PollingHealthDto> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await source.ReadAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var observation = Observe(snapshot.Metrics, now);
        var warnings = Rules(snapshot.Rules, now);
        return new PollingHealthDto(now, observation.State, observation.AtUtc, observation.Started, observation.Finished,
            observation.Age, observation.Budget, warnings.State, warnings.AtUtc, warnings.Items);
    }

    private static Observation Observe(IReadOnlyList<PollingMetricSample>? samples, DateTimeOffset now)
    {
        var unknown = new Observation("Unknown", AtUtc: null, Started: null, Finished: null, Age: null, Budget: null);
        if (samples is null || samples.Count is 0 or > 28 || samples.Any(sample => sample is null ||
            sample.Source is not { Length: > 0 and <= 128 } || !Fields.Contains(sample.Metric, StringComparer.Ordinal) ||
            !double.IsFinite(sample.Value) || sample.Value < 0 || sample.SampledAtUtc < now.AddSeconds(-60) || sample.SampledAtUtc > now))
        {
            return unknown;
        }

        var observations = new List<(bool Enabled, Observation Value)>();
        foreach (var group in samples.GroupBy(static value => value.Source, StringComparer.Ordinal))
        {
            if (group.Count() != Fields.Length || group.Select(static value => value.Metric).Distinct(StringComparer.Ordinal).Count() != Fields.Length)
            {
                return unknown;
            }

            var values = group.ToDictionary(static value => value.Metric, static value => value.Value, StringComparer.Ordinal);
            var enabled = values["enabled"];
            var state = values["state"];
            var budget = values["cycle_budget_seconds"];
            var process = Timestamp(values["started_timestamp_seconds"], now);
            var observed = Timestamp(values["observed_timestamp_seconds"], now);
            var started = Timestamp(values["last_started_timestamp_seconds"], now);
            var finished = Timestamp(values["last_finished_timestamp_seconds"], now);
            if (enabled is not (0 or 1) || state is < 0 or > 4 || !state.Equals(Math.Floor(state)) || budget is < 300 or > 86400 ||
                process is null || observed is null || observed < process ||
                values["last_started_timestamp_seconds"] != 0 && (started is null || started < process) ||
                values["last_finished_timestamp_seconds"] != 0 && (finished is null || finished < process || started is null) ||
                enabled is 0 && state is not 3 || enabled is 1 && state is 3 || state is 1 or 2 && finished is null)
            {
                return unknown;
            }

            var age = finished is { } completion ? (now - completion).TotalSeconds : (double?)null;
            var stale = (now - (finished ?? process.Value)).TotalSeconds > budget;
            var label = enabled is 0 ? "Disabled" : stale ? "Stale" : state switch { 1 => "Running", 2 => "Error", 4 => "Stale", _ => "Unknown" };
            observations.Add((enabled is 1, new Observation(label, observed, started, finished, age, budget)));
        }

        var active = observations.Where(item => item.Enabled && item.Value.AtUtc >= now.AddSeconds(-120)).ToArray();
        if (active.Length == 1)
        {
            return active[0].Value;
        }

        if (active.Length > 1)
        {
            return unknown;
        }

        var retained = observations.Where(static item => item.Enabled).Select(static item => item.Value.AtUtc).Max();
        if (retained is not null)
        {
            return unknown with { State = "Stale", AtUtc = retained };
        }

        var at = observations.Max(static item => item.Value.AtUtc);
        return unknown with { State = at < now.AddSeconds(-120) ? "Stale" : "Disabled", AtUtc = at };
    }

    private static DateTimeOffset? Timestamp(double value, DateTimeOffset now) =>
        value is >= 1577836800 and <= 253402300799 && value <= (now - DateTimeOffset.UnixEpoch).TotalSeconds
            ? DateTimeOffset.UnixEpoch.AddSeconds(value) : null;

    private static (string State, DateTimeOffset? AtUtc, IReadOnlyList<PollingWarningDto> Items) Rules(IReadOnlyList<PollingRuleSample>? samples, DateTimeOffset now)
    {
        if (samples is null || samples.Count != Names.Length || samples.Any(static item => item is null) ||
            samples.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() != Names.Length ||
            samples.Any(item => !Names.Contains(item.Name, StringComparer.Ordinal) || item.Group is not "goldsrcops-polling-liveness" ||
                item.Component is not "polling-liveness" || item.Severity is not "warning" || item.Health is not "ok" ||
                item.State is not ("inactive" or "pending" or "firing") || item.EvaluatedAtUtc > now || item.EvaluatedAtUtc < DateTimeOffset.UnixEpoch))
        {
            return ("Unknown", null, []);
        }

        var at = samples.Min(static item => item.EvaluatedAtUtc);
        return at < now.AddSeconds(-60) ? ("Stale", at, []) : ("Available", at,
            [.. samples.Select(static item => new PollingWarningDto(item.Name, item.State switch { "firing" => "Firing", "pending" => "Pending", _ => "Inactive" }))]);
    }

    private sealed record Observation(string State, DateTimeOffset? AtUtc, DateTimeOffset? Started, DateTimeOffset? Finished, double? Age, double? Budget);
}
