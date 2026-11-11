using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class ArchiveHealthServiceTests
{
    [Fact]
    public async Task Creation_timestamp_establishes_age_while_rule_states_remain_independent()
    {
        var rules = ArchiveHealthFixture.Rules().Select(sample => sample.Name is "ArchiveScheduleDelayed" ? sample with { State = "firing" } : sample).ToArray();
        var result = await ReadAsync(ArchiveHealthFixture.Metrics(600), rules);
        result.ObservationState.Should().Be("Available");
        result.LastSuccessAtUtc.Should().Be(ArchiveHealthFixture.Now.AddHours(-3));
        result.LastSuccessAgeSeconds.Should().Be(10800);
        result.WarningsState.Should().Be("Available");
        result.Warnings.Should().HaveCount(4);
        result.Warnings[0].Should().Be(new ArchiveWarningDto("ArchiveScheduleDelayed", "Firing"));
    }

    [Theory]
    [InlineData(1800, "Available")]
    [InlineData(1800.001, "Stale")]
    [InlineData(-0.001, "Unknown")]
    public async Task Observation_uses_its_own_strict_time_boundary(double age, string expected)
    {
        var result = await ReadAsync(ArchiveHealthFixture.Metrics(age), ArchiveHealthFixture.Rules());
        result.ObservationState.Should().Be(expected);
        if (expected is not "Available")
        {
            result.LastSuccessAtUtc.Should().BeNull();
            result.LastSuccessAgeSeconds.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative")]
    [InlineData("invalid-boolean")]
    [InlineData("future-success")]
    [InlineData("success-after-observer")]
    [InlineData("stale-query")]
    [InlineData("future-query")]
    public async Task Unusable_metrics_do_not_refresh_a_retained_success(string scenario)
    {
        var samples = ArchiveHealthFixture.Metrics(600).ToArray();
        samples = scenario switch
        {
            "missing" => samples[..^1],
            "duplicate" => [samples[0], samples[1], samples[2], samples[2]],
            "foreign" => [samples[0] with { Metric = "private-sentinel" }, .. samples[1..]],
            "nan" => [samples[0] with { Value = double.NaN }, .. samples[1..]],
            "infinity" => [samples[0] with { Value = double.PositiveInfinity }, .. samples[1..]],
            "negative" => [samples[0] with { Value = -1 }, .. samples[1..]],
            "invalid-boolean" => [samples[0] with { Value = 2 }, .. samples[1..]],
            "future-success" => [.. samples[..^1], samples[^1] with { Value = ArchiveHealthFixture.Now.AddSeconds(1).ToUnixTimeSeconds() }],
            "success-after-observer" => [.. samples[..^1], samples[^1] with { Value = ArchiveHealthFixture.Now.AddMinutes(-1).ToUnixTimeSeconds() }],
            "stale-query" => samples.Select(static sample => sample with { SampledAtUtc = ArchiveHealthFixture.Now.AddSeconds(-60.001) }).ToArray(),
            _ => samples.Select(static sample => sample with { SampledAtUtc = ArchiveHealthFixture.Now.AddTicks(1) }).ToArray()
        };
        var result = await ReadAsync(samples, ArchiveHealthFixture.Rules());
        result.ObservationState.Should().Be("Unknown");
        result.HasScheduledSuccess.Should().BeNull();
        result.LastSuccessAgeSeconds.Should().BeNull();
        result.WarningsState.Should().Be("Available");
    }

    [Fact]
    public async Task Failed_observation_masks_old_values_and_accepts_a_two_field_failure()
    {
        foreach (var samples in new[]
        {
            ArchiveHealthFixture.Metrics().Select(static sample => sample.Metric is "observation_success" ? sample with { Value = 0 } : sample).ToArray(),
            new[] { new ArchiveMetricSample("observation_success", 0, ArchiveHealthFixture.Now), new ArchiveMetricSample("observed_timestamp_seconds", ArchiveHealthFixture.Now.ToUnixTimeSeconds(), ArchiveHealthFixture.Now) }
        })
        {
            var result = await ReadAsync(samples, ArchiveHealthFixture.Rules());
            result.ObservationState.Should().Be("Unknown");
            result.ObservedAtUtc.Should().Be(ArchiveHealthFixture.Now);
            result.LastSuccessAtUtc.Should().BeNull();
        }
    }

    [Fact]
    public async Task No_accepted_schedule_history_is_distinct_from_a_zero_second_age()
    {
        var missing = ArchiveHealthFixture.Metrics().Select(static sample => sample.Metric is "has_success" or "last_success_timestamp_seconds" ? sample with { Value = 0 } : sample).ToArray();
        var result = await ReadAsync(missing, rules: null);
        result.ObservationState.Should().Be("Available");
        result.HasScheduledSuccess.Should().BeFalse();
        result.LastSuccessAgeSeconds.Should().BeNull();
        var zero = ArchiveHealthFixture.Metrics().Select(static sample => sample.Metric is "last_success_timestamp_seconds" ? sample with { Value = ArchiveHealthFixture.Now.ToUnixTimeSeconds() } : sample).ToArray();
        (await ReadAsync(zero, rules: null)).LastSuccessAgeSeconds.Should().Be(0);
    }

    [Theory]
    [InlineData(60, "Available")]
    [InlineData(60.001, "Stale")]
    [InlineData(-0.001, "Unknown")]
    public async Task Rules_have_their_own_time_boundary(double age, string expected)
    {
        var result = await ReadAsync(samples: null, ArchiveHealthFixture.Rules(age));
        result.ObservationState.Should().Be("Unknown");
        result.WarningsState.Should().Be(expected);
        result.Warnings.Should().HaveCount(expected is "Available" ? 4 : 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("name")]
    [InlineData("group")]
    [InlineData("component")]
    [InlineData("severity")]
    [InlineData("health")]
    [InlineData("state")]
    public async Task Incomplete_or_untrusted_rules_do_not_establish_inactive_health(string scenario)
    {
        var rules = ArchiveHealthFixture.Rules().ToArray();
        if (scenario is "missing")
        {
            rules = rules[..^1];
        }
        else
        {
            rules[0] = scenario switch
            {
                "duplicate" => rules[1],
                "name" => rules[0] with { Name = "private-sentinel" },
                "group" => rules[0] with { Group = "foreign" },
                "component" => rules[0] with { Component = "foreign" },
                "severity" => rules[0] with { Severity = "critical" },
                "health" => rules[0] with { Health = "err" },
                _ => rules[0] with { State = "silenced" }
            };
        }
        var result = await ReadAsync(ArchiveHealthFixture.Metrics(), rules);
        result.ObservationState.Should().Be("Available");
        result.WarningsState.Should().Be("Unknown");
        result.Warnings.Should().BeEmpty();
    }

    private static Task<ArchiveHealthDto> ReadAsync(IReadOnlyList<ArchiveMetricSample>? samples, IReadOnlyList<ArchiveRuleSample>? rules)
    {
        var source = new Mock<IArchiveHealthSource>();
        source.Setup(static item => item.ReadAsync(ArchiveHealthFixture.Now, It.IsAny<CancellationToken>())).ReturnsAsync(new ArchiveHealthSnapshot(samples, rules));
        var clock = new Mock<IClock>();
        clock.SetupGet(static item => item.UtcNow).Returns(ArchiveHealthFixture.Now);
        return new ArchiveHealthService(source.Object, clock.Object).ReadAsync(CancellationToken.None);
    }
}
