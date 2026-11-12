using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

internal static class PollingHealthFixture
{
    private static readonly string[] Names = ["PollingMonitorUnavailable", "PollingCycleStalled", "PollingCycleFailed"];
    public static DateTimeOffset Now { get; } = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public static PollingMetricSample[] Metrics(double state = 1) =>
    [
        Sample("enabled", state is 3 ? 0 : 1), Sample("state", state), Sample("cycle_budget_seconds", 370),
        Sample("started_timestamp_seconds", Now.AddHours(-1).ToUnixTimeSeconds()),
        Sample("observed_timestamp_seconds", Now.ToUnixTimeSeconds()),
        Sample("last_started_timestamp_seconds", state is 3 ? 0 : Now.AddSeconds(-6).ToUnixTimeSeconds()),
        Sample("last_finished_timestamp_seconds", state is 3 ? 0 : Now.AddSeconds(-5).ToUnixTimeSeconds())
    ];

    private static PollingMetricSample Sample(string field, double value) => new("active", field, value, Now);

    public static PollingRuleSample[] Rules() => [.. Names
        .Select(static name => new PollingRuleSample(name, "goldsrcops-polling-liveness", "polling-liveness", "warning", "inactive", "ok", Now))];
}
