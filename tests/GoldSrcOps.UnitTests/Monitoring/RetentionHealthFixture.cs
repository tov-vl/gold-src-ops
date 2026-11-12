using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

internal static class RetentionHealthFixture
{
    private static readonly string[] Names = ["SnapshotRetentionMonitorUnavailable", "SnapshotRetentionCycleStalled", "SnapshotRetentionCycleFailed"];
    public static DateTimeOffset Now { get; } = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public static RetentionMetricSample[] Metrics(double state = 1) =>
    [
        Sample("enabled", state is 3 ? 0 : 1), Sample("state", state), Sample("cycle_budget_seconds", 370),
        Sample("started_timestamp_seconds", Now.AddHours(-1).ToUnixTimeSeconds()),
        Sample("observed_timestamp_seconds", Now.ToUnixTimeSeconds()),
        Sample("last_started_timestamp_seconds", state is 3 ? 0 : Now.AddSeconds(-6).ToUnixTimeSeconds()),
        Sample("last_finished_timestamp_seconds", state is 3 ? 0 : Now.AddSeconds(-5).ToUnixTimeSeconds()),
        Sample("last_successful_cycle_timestamp_seconds", state is 3 ? 0 : Now.AddSeconds(-5).ToUnixTimeSeconds()),
        Sample("last_failure_timestamp_seconds", 0), Sample("last_cycle_duration_seconds", state is 3 ? 0 : 1),
        Sample("consecutive_failures", state is 2 ? 2 : 0), Sample("in_progress", 0)
    ];

    private static RetentionMetricSample Sample(string field, double value) => new("active", field, value, Now);

    public static RetentionRuleSample[] Rules() => [.. Names
        .Select(static name => new RetentionRuleSample(name, "goldsrcops-snapshot-retention-liveness", "snapshot-retention-liveness", "warning", "inactive", "ok", Now))];
}
