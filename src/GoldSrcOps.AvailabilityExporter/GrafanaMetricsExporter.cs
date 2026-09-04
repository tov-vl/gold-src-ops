using GoldSrcOps.Application.Availability;

namespace GoldSrcOps.AvailabilityExporter;

internal sealed class GrafanaMetricsExporter
{
    private const string ProviderName = "grafana-cloud";

    private readonly GrafanaMetricsApiClient _client;
    private readonly GrafanaMetricsApiOptions _options;
    private readonly IProbeFailureDetailSource? _failureDetailSource;

    public GrafanaMetricsExporter(
        GrafanaMetricsApiClient client,
        GrafanaMetricsApiOptions options,
        IProbeFailureDetailSource? failureDetailSource = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (failureDetailSource is not null &&
            (failureDetailSource.CorrelationTolerance <= TimeSpan.Zero ||
             failureDetailSource.CorrelationTolerance > TimeSpan.FromSeconds(30)))
        {
            throw new ArgumentOutOfRangeException(nameof(failureDetailSource));
        }

        _client = client;
        _options = options;
        _failureDetailSource = failureDetailSource;
    }

    public async Task<IReadOnlyList<CanonicalAvailabilityResult>> ExportAsync(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        ValidateRange(startUtc, endUtc);

        var successValues = await QueryAsync(
            "probe_success",
            returnSourceTimestamp: false,
            startUtc,
            endUtc,
            cancellationToken).ConfigureAwait(false);
        var successTimestamps = await QueryAsync(
            "probe_success",
            returnSourceTimestamp: true,
            startUtc,
            endUtc,
            cancellationToken).ConfigureAwait(false);
        var statusValues = await QueryAsync(
            "probe_http_status_code",
            returnSourceTimestamp: false,
            startUtc,
            endUtc,
            cancellationToken).ConfigureAwait(false);
        var statusTimestamps = await QueryAsync(
            "probe_http_status_code",
            returnSourceTimestamp: true,
            startUtc,
            endUtc,
            cancellationToken).ConfigureAwait(false);
        var durationValues = await QueryAsync(
            "probe_duration_seconds",
            returnSourceTimestamp: false,
            startUtc,
            endUtc,
            cancellationToken).ConfigureAwait(false);
        var durationTimestamps = await QueryAsync(
            "probe_duration_seconds",
            returnSourceTimestamp: true,
            startUtc,
            endUtc,
            cancellationToken).ConfigureAwait(false);

        var successes = CorrelateBySourceTimestamp(successValues, successTimestamps);
        var statuses = CorrelateBySourceTimestamp(statusValues, statusTimestamps);
        var durations = CorrelateBySourceTimestamp(durationValues, durationTimestamps);

        EnsureSingleCheckIdentity(successes.Keys);

        IReadOnlyList<ProbeFailureDetail> failureDetails = [];
        if (_failureDetailSource is not null && RequiresFailureDetails(successes, statuses))
        {
            failureDetails = await _failureDetailSource.QueryAsync(
                startUtc,
                endUtc,
                cancellationToken).ConfigureAwait(false);
        }

        var context = new AvailabilityNormalizationContext(
            ProviderName,
            _options.MonitorRevision,
            _options.Location,
            ParseRole(_options.Role));
        var normalized = new Dictionary<string, CanonicalAvailabilityResult>(StringComparer.Ordinal);

        foreach (var success in successes.OrderBy(pair => pair.Key.SourceTimestampUtc))
        {
            var scheduledAtUtc = FloorToMinute(success.Key.SourceTimestampUtc);
            if (scheduledAtUtc < startUtc || scheduledAtUtc >= endUtc)
            {
                continue;
            }

            var succeeded = ParseSuccess(success.Value);
            var httpStatus = statuses.TryGetValue(success.Key, out var status)
                ? ParseHttpStatus(status)
                : null;
            TimeSpan? duration = durations.TryGetValue(success.Key, out var durationSeconds)
                ? ParseDuration(durationSeconds)
                : null;
            var failureKind = ResolveFailureKind(
                success.Key.SourceTimestampUtc,
                succeeded,
                httpStatus,
                failureDetails);
            var record = AvailabilityNormalizer.Normalize(
                new ProviderProbeSample(
                    success.Key.SourceTimestampUtc,
                    succeeded,
                    httpStatus,
                    duration,
                    FailureKind: failureKind,
                    CompletedAtUtc: success.Key.SourceTimestampUtc),
                context);

            if (normalized.TryGetValue(record.ExecutionId, out var existing) && existing != record)
            {
                throw new InvalidDataException(
                    "The metrics API returned conflicting values for one probe execution.");
            }

            normalized[record.ExecutionId] = record;
        }

        return normalized.Values
            .OrderBy(record => record.ScheduledAtUtc)
            .ThenBy(record => record.StartedAtUtc ?? record.CompletedAtUtc)
            .ThenBy(record => record.ExecutionId, StringComparer.Ordinal)
            .ToArray();
    }

    private Task<IReadOnlyList<PrometheusSeries>> QueryAsync(
        string metric,
        bool returnSourceTimestamp,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken) =>
        _client.QueryRangeAsync(
            PromQlQueryBuilder.Build(metric, _options, returnSourceTimestamp),
            startUtc,
            endUtc,
            cancellationToken);

    private static Dictionary<MetricObservationKey, double> CorrelateBySourceTimestamp(
        IReadOnlyList<PrometheusSeries> valueSeries,
        IReadOnlyList<PrometheusSeries> timestampSeries)
    {
        var timestampsByEvaluation = FlattenByEvaluationTimestamp(timestampSeries);
        var observations = new Dictionary<MetricObservationKey, double>();

        foreach (var series in valueSeries)
        {
            foreach (var sample in series.Samples)
            {
                var evaluationKey = (series.Key, sample.EvaluatedAtUtc);
                if (!timestampsByEvaluation.TryGetValue(evaluationKey, out var sourceTimestampSeconds))
                {
                    throw new InvalidDataException(
                        "A metric value did not have a matching source timestamp.");
                }

                var key = new MetricObservationKey(
                    series.Key,
                    PrometheusMatrixParser.FromUnixSeconds(sourceTimestampSeconds));
                if (observations.TryGetValue(key, out var existing) && existing != sample.Value)
                {
                    throw new InvalidDataException(
                        "A metric changed value for the same source timestamp.");
                }

                observations[key] = sample.Value;
            }
        }

        return observations;
    }

    private static Dictionary<(PrometheusSeriesKey Series, DateTimeOffset EvaluatedAtUtc), double>
        FlattenByEvaluationTimestamp(IReadOnlyList<PrometheusSeries> seriesCollection)
    {
        var samples = new Dictionary<(PrometheusSeriesKey, DateTimeOffset), double>();

        foreach (var series in seriesCollection)
        {
            foreach (var sample in series.Samples)
            {
                var key = (series.Key, sample.EvaluatedAtUtc);
                if (samples.TryGetValue(key, out var existing) && existing != sample.Value)
                {
                    throw new InvalidDataException(
                        "The metrics API returned conflicting duplicate samples.");
                }

                samples[key] = sample.Value;
            }
        }

        return samples;
    }

    private static bool RequiresFailureDetails(
        IReadOnlyDictionary<MetricObservationKey, double> successes,
        Dictionary<MetricObservationKey, double> statuses)
    {
        foreach (var success in successes)
        {
            if (!ParseSuccess(success.Value) &&
                (!statuses.TryGetValue(success.Key, out var status) || ParseHttpStatus(status) is null))
            {
                return true;
            }
        }

        return false;
    }

    private ProbeFailureKind? ResolveFailureKind(
        DateTimeOffset sourceTimestampUtc,
        bool succeeded,
        int? httpStatus,
        IReadOnlyList<ProbeFailureDetail> failureDetails)
    {
        if (succeeded || httpStatus is not null || _failureDetailSource is null)
        {
            return null;
        }

        var tolerance = _failureDetailSource.CorrelationTolerance;
        var matchingKinds = failureDetails
            .Where(detail =>
            {
                var difference = detail.ObservedAtUtc - sourceTimestampUtc;
                return difference >= -tolerance && difference <= tolerance;
            })
            .Select(detail => detail.FailureKind)
            .Distinct()
            .Take(2)
            .ToArray();

        return matchingKinds.Length switch
        {
            0 => ProbeFailureKind.Monitor,
            1 => matchingKinds[0],
            _ => ProbeFailureKind.Monitor,
        };
    }

    private static void EnsureSingleCheckIdentity(IEnumerable<MetricObservationKey> keys)
    {
        var identities = keys
            .Select(key => (key.Series.Job, key.Series.Instance, key.Series.Probe))
            .Distinct()
            .Take(2)
            .Count();

        if (identities > 1)
        {
            throw new InvalidDataException(
                "The metrics query matched more than one synthetic check identity.");
        }
    }

    private static AvailabilityProbeRole ParseRole(string role) => role.ToUpperInvariant() switch
    {
        "PRIMARY" => AvailabilityProbeRole.Primary,
        "DIAGNOSTIC" => AvailabilityProbeRole.Diagnostic,
        _ => throw new InvalidDataException("The configured probe role is unsupported."),
    };

    private static bool ParseSuccess(double value) => value switch
    {
        0d => false,
        1d => true,
        _ => throw new InvalidDataException("The success metric must be zero or one."),
    };

    private static int? ParseHttpStatus(double value)
    {
        if (value == 0d)
        {
            return null;
        }

        var status = checked((int)value);
        if (status != value || status is < 100 or > 599)
        {
            throw new InvalidDataException("The HTTP status metric is invalid.");
        }

        return status;
    }

    private static TimeSpan ParseDuration(double seconds)
    {
        if (seconds < 0d || seconds > TimeSpan.FromDays(1).TotalSeconds)
        {
            throw new InvalidDataException("The duration metric is outside the supported range.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static void ValidateRange(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (startUtc.Offset != TimeSpan.Zero || endUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The export range must use UTC.", nameof(startUtc));
        }

        if (endUtc <= startUtc)
        {
            throw new ArgumentException("The export range must not be empty.", nameof(endUtc));
        }
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            utc.Minute,
            0,
            TimeSpan.Zero);
    }
}
