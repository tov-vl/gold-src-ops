using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class ArchiveHealthViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("valid", "Available", "Available")]
    [InlineData("missing", "Unknown", "Unknown")]
    [InlineData("stale-response", "Unknown", "Unknown")]
    [InlineData("future-response", "Unknown", "Unknown")]
    [InlineData("old-observer", "Stale", "Available")]
    [InlineData("observer-boundary", "Available", "Available")]
    [InlineData("future-observer", "Unknown", "Available")]
    [InlineData("false-age", "Unknown", "Available")]
    [InlineData("failed-observer", "Unknown", "Available")]
    [InlineData("stale-rules", "Available", "Stale")]
    [InlineData("future-rules", "Available", "Unknown")]
    [InlineData("missing-rules", "Available", "Unknown")]
    [InlineData("duplicate-rules", "Available", "Unknown")]
    [InlineData("foreign-rules", "Available", "Unknown")]
    [InlineData("foreign-state", "Available", "Unknown")]
    [InlineData("null-rule", "Available", "Unknown")]
    public void Normalization_preserves_separate_validity_and_never_reflects_foreign_rules(string scenario, string observation, string warnings)
    {
        ArchiveHealthResponse? source = Valid();
        source = scenario switch
        {
            "missing" => null,
            "stale-response" => source with { EvaluatedAtUtc = Now.AddSeconds(-60.001) },
            "future-response" => source with { EvaluatedAtUtc = Now.AddTicks(1) },
            "old-observer" => source with { ObservedAtUtc = Now.AddSeconds(-1800.001) },
            "observer-boundary" => source with { ObservedAtUtc = Now.AddSeconds(-1800) },
            "future-observer" => source with { ObservedAtUtc = Now.AddTicks(1) },
            "false-age" => source with { LastSuccessAgeSeconds = 0 },
            "failed-observer" => source with { ObservationState = "Unknown" },
            "stale-rules" => source with { WarningsObservedAtUtc = Now.AddSeconds(-60.001) },
            "future-rules" => source with { WarningsObservedAtUtc = Now.AddTicks(1) },
            "missing-rules" => source with { Warnings = source.Warnings.Take(3).ToArray() },
            "duplicate-rules" => source with { Warnings = [source.Warnings[0], source.Warnings[0], source.Warnings[2], source.Warnings[3]] },
            "foreign-rules" => source with { Warnings = [new ArchiveWarningResponse("private-sentinel", "Firing"), .. source.Warnings.Skip(1)] },
            "foreign-state" => source with { Warnings = [source.Warnings[0] with { State = "private-sentinel" }, .. source.Warnings.Skip(1)] },
            "null-rule" => source with { Warnings = [null!, .. source.Warnings.Skip(1)] },
            _ => source
        };
        var result = ArchiveHealthView.Create(source, Now);
        result.ObservationState.Should().Be(observation);
        result.WarningsState.Should().Be(warnings);
        if (observation is not "Available")
        {
            result.LastSuccessAgeSeconds.Should().BeNull();
            result.LastSuccessAtUtc.Should().BeNull();
        }

        result.Warnings.Should().HaveCount(warnings is "Available" ? 4 : 0);
        result.Warnings.Should().NotContain(static item => item.Name.Contains("private-sentinel", StringComparison.Ordinal));
    }

    [Fact]
    public void Age_advances_with_view_time_and_empty_history_keeps_age_unknown()
    {
        ArchiveHealthView.Create(Valid(), Now.AddSeconds(10)).LastSuccessAgeSeconds.Should().Be(10810);
        var result = ArchiveHealthView.Create(Valid() with { HasScheduledSuccess = false, LastSuccessAgeSeconds = null, LastSuccessAtUtc = null }, Now);
        result.ObservationState.Should().Be("Available");
        result.HasScheduledSuccess.Should().BeFalse();
        result.LastSuccessAgeSeconds.Should().BeNull();
    }

    [Fact]
    public void Nonfinite_age_is_unknown_and_exact_rule_boundary_is_available()
    {
        foreach (var age in new[] { double.NaN, double.PositiveInfinity, -1 })
        {
            ArchiveHealthView.Create(Valid() with { LastSuccessAgeSeconds = age }, Now).ObservationState.Should().Be("Unknown");
        }

        ArchiveHealthView.Create(Valid() with { WarningsObservedAtUtc = Now.AddSeconds(-60) }, Now).WarningsState.Should().Be("Available");
    }

    private static ArchiveHealthResponse Valid() => new(Now, "Available", Now, HasScheduledSuccess: true, Now.AddHours(-3), 10800, "Available", Now,
        [.. ArchiveHealthView.Definitions.Select(static item => new ArchiveWarningResponse(item.Name, "Inactive"))]);
}
