using GoldSrcOps.Application.Monitoring;

namespace GoldSrcOps.UnitTests.Monitoring;

internal static class ArchiveHealthFixture
{
    public static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Names = ["ArchiveScheduleMonitorUnavailable", "ArchiveScheduleObservationInvalid", "ArchiveScheduleDelayed", "ArchiveScheduleCatchupAtRisk"];
    public static IReadOnlyList<ArchiveMetricSample> Metrics(double observationAge = 0) =>
    [
        new("observation_success", 1, Now), new("observed_timestamp_seconds", Now.ToUnixTimeSeconds() - observationAge, Now),
        new("has_success", 1, Now), new("last_success_timestamp_seconds", Now.AddHours(-3).ToUnixTimeSeconds(), Now)
    ];
    public static IReadOnlyList<ArchiveRuleSample> Rules(double evaluationAge = 0) =>
        Names
            .Select(name => new ArchiveRuleSample(name, "goldsrcops-archive-schedule", "archive-schedule", "warning", "inactive", "ok", Now.AddSeconds(-evaluationAge))).ToArray();
}
