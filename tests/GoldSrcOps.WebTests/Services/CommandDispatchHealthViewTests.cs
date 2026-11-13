using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Web.Services;

namespace GoldSrcOps.WebTests.Services;

public sealed class CommandDispatchHealthViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Running")]
    [InlineData("Error")]
    [InlineData("Unknown")]
    [InlineData("Disabled")]
    [InlineData("Stale")]
    public void Known_states_and_independent_firing_warning_survive_validation(string state)
    {
        var source = Valid() with { ObservationState = state };
        var result = CommandDispatchHealthView.Create(source, Now);
        result.ObservationState.Should().Be(state);
        result.Warnings.Should().HaveCount(3).And.Contain(static item => item.State == "Firing");
    }

    [Theory]
    [InlineData("future-response")]
    [InlineData("old-response")]
    [InlineData("foreign-state")]
    [InlineData("future-observer")]
    [InlineData("nonfinite-age")]
    [InlineData("wrong-age")]
    [InlineData("no-completion")]
    [InlineData("bad-budget")]
    public void Bad_or_foreign_observations_do_not_reflect_retained_or_provider_values(string scenario)
    {
        var source = scenario switch
        {
            "future-response" => Valid() with { EvaluatedAtUtc = Now.AddSeconds(1) },
            "old-response" => Valid() with { EvaluatedAtUtc = Now.AddSeconds(-61) },
            "foreign-state" => Valid() with { ObservationState = "private-sentinel" },
            "future-observer" => Valid() with { ObservedAtUtc = Now.AddSeconds(1) },
            "nonfinite-age" => Valid() with { CompletionAgeSeconds = double.PositiveInfinity },
            "wrong-age" => Valid() with { CompletionAgeSeconds = 0 },
            "no-completion" => Valid() with { LastCycleFinishedAtUtc = null, CompletionAgeSeconds = null },
            _ => Valid() with { CycleBudgetSeconds = 299 }
        };
        var result = CommandDispatchHealthView.Create(source, Now);
        result.ObservationState.Should().Be("Unknown");
        result.CompletionAgeSeconds.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain("private-sentinel");
    }

    [Fact]
    public void Independent_observer_and_completion_callbacks_do_not_require_an_atomic_export()
    {
        var result = CommandDispatchHealthView.Create(Valid() with { ObservedAtUtc = Now.AddSeconds(-10) }, Now);
        result.ObservationState.Should().Be("Running");
        result.CompletionAgeSeconds.Should().Be(5);
    }

    [Fact]
    public void Fresh_query_does_not_refresh_observer_and_age_is_recalculated_without_changing_rule_state()
    {
        var stale = CommandDispatchHealthView.Create(Valid() with { ObservedAtUtc = Now.AddSeconds(-121) }, Now);
        stale.ObservationState.Should().Be("Stale");
        stale.LastCycleFinishedAtUtc.Should().BeNull();
        var source = Valid() with { LastCycleStartedAtUtc = Now.AddSeconds(-301), LastCycleFinishedAtUtc = Now.AddSeconds(-300), CompletionAgeSeconds = 300, CycleBudgetSeconds = 300 };
        var result = CommandDispatchHealthView.Create(source, Now.AddSeconds(1));
        result.ObservationState.Should().Be("Stale");
        result.CompletionAgeSeconds.Should().Be(301);
        result.WarningsState.Should().Be("Available");
    }

    [Fact]
    public void Incomplete_and_old_rules_do_not_hide_running_observation()
    {
        var source = Valid();
        var unknown = CommandDispatchHealthView.Create(source with { Warnings = [source.Warnings[0], source.Warnings[0], source.Warnings[2]] }, Now);
        unknown.ObservationState.Should().Be("Running");
        unknown.WarningsState.Should().Be("Unknown");
        unknown.Warnings.Should().BeEmpty();
        var stale = CommandDispatchHealthView.Create(source with { WarningsObservedAtUtc = Now.AddSeconds(-61) }, Now);
        stale.ObservationState.Should().Be("Running");
        stale.WarningsState.Should().Be("Stale");
        stale.Warnings.Should().BeEmpty();
    }

    private static CommandDispatchHealthResponse Valid() => new(Now, "Running", Now, Now.AddSeconds(-6), Now.AddSeconds(-5), 5, 370,
        "Available", Now, [.. CommandDispatchHealthView.Definitions.Select(static item => new CommandDispatchWarningResponse(item.Name, item.Name is "CommandDispatchCycleFailed" ? "Firing" : "Inactive"))], LastCycleDurationSeconds: 1, ConsecutiveFailures: 0, InProgress: false);

    [Fact]
    public void Invalid_additional_metadata_fails_independently_of_warnings()
    {
        foreach (var source in new[]
        {
            Valid() with { LastSuccessfulCycleAtUtc = Now.AddSeconds(1) },
            Valid() with { LastFailureAtUtc = Now.AddSeconds(1) },
            Valid() with { LastCycleDurationSeconds = double.PositiveInfinity },
            Valid() with { ConsecutiveFailures = 1000001 },
            Valid() with { InProgress = null }
        })
        {
            var result = CommandDispatchHealthView.Create(source, Now);
            result.ObservationState.Should().Be("Unknown");
            result.LastCycleDurationSeconds.Should().BeNull();
            result.WarningsState.Should().Be("Available");
        }
    }
}
