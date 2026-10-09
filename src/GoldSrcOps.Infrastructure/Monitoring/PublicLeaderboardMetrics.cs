using System.Diagnostics.Metrics;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Application.Telemetry;

namespace GoldSrcOps.Infrastructure.Monitoring;

internal enum PublicLeaderboardPollResult
{
    None,
    Success,
    OperatorBusy,
    SourceUnavailable,
    CredentialUnavailable,
    Timeout,
    AuthenticationFailed,
    InvalidFrame,
    Failed,
    Disabled
}

internal sealed class PublicLeaderboardMetrics : IDisposable
{
    private static readonly string[] States = ["fresh", "stale", "unavailable", "disabled"];
    private static readonly PublicLeaderboardPollResult[] Results = Enum.GetValues<PublicLeaderboardPollResult>();
    private readonly Meter meter;
    private readonly Counter<int> attempts;
    private readonly Histogram<double> duration;
    private readonly IClock clock;
    private readonly System.Threading.Lock gate = new();
    private Observation observation = new(0, 0, PublicLeaderboardPollResult.None);

    public PublicLeaderboardMetrics(PublicLeaderboardSettings settings, PublicLeaderboardStore store, IClock clock)
    {
        this.clock = clock;
        meter = new(new MeterOptions(GoldSrcOpsMetrics.MeterName) { Scope = store });
        attempts = meter.CreateCounter<int>("goldsrcops.leaderboard.poll_attempts",
            description: "Completed leaderboard passes by bounded result.");
        duration = meter.CreateHistogram<double>("goldsrcops.leaderboard.poll_duration", unit: "s",
            description: "Duration of leaderboard passes by bounded result.");
        var started = Seconds(clock.UtcNow);
        meter.CreateObservableGauge("goldsrcops.leaderboard.enabled", () => settings.Enabled ? 1 : 0);
        meter.CreateObservableGauge("goldsrcops.leaderboard.started_timestamp", () => started, unit: "s");
        meter.CreateObservableGauge("goldsrcops.leaderboard.observed_timestamp", () => Seconds(clock.UtcNow), unit: "s");
        meter.CreateObservableGauge("goldsrcops.leaderboard.last_attempt_timestamp", () => ReadObservation().LastAttempt, unit: "s");
        meter.CreateObservableGauge("goldsrcops.leaderboard.last_success_timestamp", () => ReadObservation().LastSuccess, unit: "s");
        meter.CreateObservableGauge("goldsrcops.leaderboard.snapshot_available", () => settings.Enabled && store.Read().CapturedAtUtc is not null ? 1 : 0);
        meter.CreateObservableGauge("goldsrcops.leaderboard.snapshot_age", () =>
            settings.Enabled && store.Read().CapturedAtUtc is { } captured ? Math.Max(0, (clock.UtcNow - captured).TotalSeconds) : 0, unit: "s");
        meter.CreateObservableGauge("goldsrcops.leaderboard.rows", () => settings.Enabled ? store.Read().Entries.Count : 0);
        meter.CreateObservableGauge("goldsrcops.leaderboard.state", () =>
        {
            var current = settings.Enabled ? store.Read().State : "disabled";
            return States
                .Select(state => new Measurement<int>(string.Equals(current, state, StringComparison.Ordinal) ? 1 : 0, new KeyValuePair<string, object?>("state", state)));
        });
        meter.CreateObservableGauge("goldsrcops.leaderboard.last_result", () =>
        {
            var current = ReadObservation().Result;
            return Results
                .Select(result => new Measurement<int>(current == result ? 1 : 0, new KeyValuePair<string, object?>("result", ResultLabel(result))));
        });
    }

    public void RecordAttempt(PublicLeaderboardPollResult result, TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        if (result == PublicLeaderboardPollResult.None)
        {
            throw new ArgumentOutOfRangeException(nameof(result));
        }

        var tag = new KeyValuePair<string, object?>("result", ResultLabel(result));
        lock (gate)
        {
            var now = Seconds(clock.UtcNow);
            observation = new(now, result == PublicLeaderboardPollResult.Success ? now : observation.LastSuccess, result);
        }
        attempts.Add(1, tag);
        duration.Record(elapsed.TotalSeconds, tag);
    }

    public void Dispose() => meter.Dispose();

    private Observation ReadObservation()
    {
        lock (gate)
        {
            return observation;
        }
    }

    private static double Seconds(DateTimeOffset value) => (value - DateTimeOffset.UnixEpoch).TotalSeconds;

    private static string ResultLabel(PublicLeaderboardPollResult value) => value switch
    {
        PublicLeaderboardPollResult.None => "none",
        PublicLeaderboardPollResult.Success => "success",
        PublicLeaderboardPollResult.OperatorBusy => "operator_busy",
        PublicLeaderboardPollResult.SourceUnavailable => "source_unavailable",
        PublicLeaderboardPollResult.CredentialUnavailable => "credential_unavailable",
        PublicLeaderboardPollResult.Timeout => "timeout",
        PublicLeaderboardPollResult.AuthenticationFailed => "authentication_failed",
        PublicLeaderboardPollResult.InvalidFrame => "invalid_frame",
        PublicLeaderboardPollResult.Failed => "failed",
        PublicLeaderboardPollResult.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private sealed record Observation(double LastAttempt, double LastSuccess, PublicLeaderboardPollResult Result);
}
