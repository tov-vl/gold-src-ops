using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class PollingHealthServiceTests
{
    [Theory]
    [InlineData(0, "Unknown")]
    [InlineData(1, "Running")]
    [InlineData(2, "Error")]
    [InlineData(3, "Disabled")]
    [InlineData(4, "Stale")]
    public async Task Fixed_worker_states_are_separate_from_rule_states(double state, string expected)
    {
        var result = await ReadAsync(PollingHealthFixture.Metrics(state), PollingHealthFixture.Rules());
        result.ObservationState.Should().Be(expected);
        result.WarningsState.Should().Be("Available");
        result.Warnings.Should().HaveCount(3).And.OnlyContain(static item => item.State == "Inactive");
        result.CompletionAgeSeconds.Should().Be(state is 3 ? null : 5);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("nonfinite")]
    [InlineData("fractional-state")]
    [InlineData("unknown-field")]
    [InlineData("future-sample")]
    [InlineData("old-query")]
    [InlineData("future-observer")]
    [InlineData("invalid-budget")]
    [InlineData("incoherent-disabled")]
    [InlineData("missing-source")]
    public async Task Invalid_observation_never_hides_usable_rules(string scenario)
    {
        var samples = PollingHealthFixture.Metrics();
        samples = scenario switch
        {
            "missing" => samples[..^1],
            "duplicate" => [.. samples, samples[0]],
            "nonfinite" => [samples[0] with { Value = double.NaN }, .. samples[1..]],
            "fractional-state" => Replace(samples, "state", 1.5),
            "unknown-field" => [samples[0] with { Metric = "private-sentinel" }, .. samples[1..]],
            "future-sample" => [samples[0] with { SampledAtUtc = PollingHealthFixture.Now.AddSeconds(1) }, .. samples[1..]],
            "old-query" => [.. samples.Select(static item => item with { SampledAtUtc = PollingHealthFixture.Now.AddSeconds(-61) })],
            "future-observer" => Replace(samples, "observed_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(1).ToUnixTimeSeconds()),
            "invalid-budget" => Replace(samples, "cycle_budget_seconds", 86401),
            "incoherent-disabled" => Replace(samples, "state", 3),
            _ => [samples[0] with { Source = "" }, .. samples[1..]]
        };
        var result = await ReadAsync(samples, PollingHealthFixture.Rules());
        result.ObservationState.Should().Be("Unknown");
        result.CompletionAgeSeconds.Should().BeNull();
        result.WarningsState.Should().Be("Available");
    }

    [Fact]
    public async Task Independent_gauge_callbacks_can_observe_a_completion_after_the_observer_callback()
    {
        var samples = Replace(PollingHealthFixture.Metrics(), "observed_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-10).ToUnixTimeSeconds());
        (await ReadAsync(samples, rules: null)).ObservationState.Should().Be("Running");
    }

    [Fact]
    public async Task Completion_age_is_independent_of_a_retained_running_state_and_startup_has_no_invented_completion()
    {
        var samples = Replace(PollingHealthFixture.Metrics(), "last_finished_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-371).ToUnixTimeSeconds());
        var result = await ReadAsync(samples, PollingHealthFixture.Rules());
        result.ObservationState.Should().Be("Stale");
        result.CompletionAgeSeconds.Should().Be(371);
        samples = Replace(Replace(PollingHealthFixture.Metrics(0), "last_finished_timestamp_seconds", 0), "started_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-30).ToUnixTimeSeconds());
        (await ReadAsync(samples, rules: null)).ObservationState.Should().Be("Unknown");
        (await ReadAsync(samples, rules: null)).CompletionAgeSeconds.Should().BeNull();
        samples = Replace(samples, "started_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-371).ToUnixTimeSeconds());
        (await ReadAsync(samples, rules: null)).ObservationState.Should().Be("Stale");
    }

    [Fact]
    public async Task Fresh_disabled_replica_does_not_hide_failed_active_or_stale_active_and_duplicates_are_not_merged()
    {
        var disabled = PollingHealthFixture.Metrics(3).Select(static item => item with { Source = "disabled" }).ToArray();
        var active = PollingHealthFixture.Metrics(2);
        (await ReadAsync([.. active, .. disabled], rules: null)).ObservationState.Should().Be("Error");
        var duplicate = active.Select(static item => item with { Source = "other-active" });
        (await ReadAsync([.. active, .. duplicate], rules: null)).ObservationState.Should().Be("Unknown");
        var stale = Replace(active, "observed_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-121).ToUnixTimeSeconds());
        stale = Replace(Replace(stale, "last_finished_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-130).ToUnixTimeSeconds()), "last_started_timestamp_seconds", PollingHealthFixture.Now.AddSeconds(-131).ToUnixTimeSeconds());
        (await ReadAsync([.. stale, .. disabled], rules: null)).ObservationState.Should().Be("Stale");
        (await ReadAsync([.. stale, .. active.Select(static item => item with { Source = "new-active" })], rules: null)).ObservationState.Should().Be("Error");
    }

    [Theory]
    [InlineData("missing", "Unknown")]
    [InlineData("duplicate", "Unknown")]
    [InlineData("foreign", "Unknown")]
    [InlineData("unhealthy", "Unknown")]
    [InlineData("future", "Unknown")]
    [InlineData("stale", "Stale")]
    public async Task Independent_rule_snapshot_fails_closed(string scenario, string expected)
    {
        var rules = PollingHealthFixture.Rules();
        rules = scenario switch
        {
            "missing" => rules[..^1],
            "duplicate" => [rules[0], rules[0], rules[2]],
            "foreign" => [rules[0] with { Name = "private-sentinel" }, .. rules[1..]],
            "unhealthy" => [rules[0] with { Health = "err" }, .. rules[1..]],
            "future" => [rules[0] with { EvaluatedAtUtc = PollingHealthFixture.Now.AddSeconds(1) }, .. rules[1..]],
            _ => [rules[0] with { EvaluatedAtUtc = PollingHealthFixture.Now.AddSeconds(-61) }, .. rules[1..]]
        };
        var result = await ReadAsync(PollingHealthFixture.Metrics(), rules);
        result.ObservationState.Should().Be("Running");
        result.WarningsState.Should().Be(expected);
        result.Warnings.Should().BeEmpty();
    }

    private static PollingMetricSample[] Replace(IEnumerable<PollingMetricSample> samples, string name, double value) =>
        [.. samples.Select(item => string.Equals(item.Metric, name, StringComparison.Ordinal) ? item with { Value = value } : item)];

    private static Task<PollingHealthDto> ReadAsync(IReadOnlyList<PollingMetricSample>? metrics, IReadOnlyList<PollingRuleSample>? rules)
    {
        var source = new Mock<IPollingHealthSource>();
        source.Setup(static item => item.ReadAsync(PollingHealthFixture.Now, It.IsAny<CancellationToken>())).ReturnsAsync(new PollingHealthSnapshot(metrics, rules));
        var clock = Mock.Of<IClock>(item => item.UtcNow == PollingHealthFixture.Now);
        return new PollingHealthService(source.Object, clock).ReadAsync(CancellationToken.None);
    }
}
