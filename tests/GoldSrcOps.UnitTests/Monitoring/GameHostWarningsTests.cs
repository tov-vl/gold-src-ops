using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.Common;
using GoldSrcOps.Application.Monitoring;
using Moq;

namespace GoldSrcOps.UnitTests.Monitoring;

public sealed class GameHostWarningsTests
{
    [Fact]
    public async Task Fresh_complete_inactive_rules_are_the_only_empty_success()
    {
        var result = await ReadAsync(GameHostWarningFixture.Fresh());
        result.Warnings!.State.Should().Be("Available");
        result.Warnings.ObservedAtUtc.Should().Be(GameHostWarningFixture.Now);
        result.Warnings.Items.Should().BeEmpty();
        result.Components.Should().OnlyContain(static value => value.State == "Unknown");
    }

    [Fact]
    public async Task Firing_precedes_pending_and_descriptions_are_static()
    {
        var samples = GameHostWarningFixture.Fresh();
        samples[0] = samples[0] with { State = "pending" };
        samples[7] = samples[7] with { State = "firing" };
        var result = await ReadAsync(samples);
        result.Warnings!.Items.Select(static value => value.Name).Should().Equal("PlayerBackupInterventionRequired", "GameEventAgentMonitorUnavailable");
        result.Warnings.Items[0].Description.Should().Contain("Do not retry");
        result.Warnings.Items[1].State.Should().Be("Pending");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("name")]
    [InlineData("component")]
    [InlineData("group")]
    [InlineData("severity")]
    [InlineData("health")]
    [InlineData("state")]
    [InlineData("future")]
    public async Task Partial_ambiguous_or_invalid_rules_never_claim_an_empty_success(string kind)
    {
        var samples = GameHostWarningFixture.Fresh();
        samples[0] = kind switch
        {
            "duplicate" => samples[1],
            "name" => samples[0] with { Name = "private-label-sentinel" },
            "component" => samples[0] with { Component = "private-label-sentinel" },
            "group" => samples[0] with { Group = "private-label-sentinel" },
            "severity" => samples[0] with { Severity = "critical" },
            "health" => samples[0] with { Health = "err" },
            "state" => samples[0] with { State = "resolved" },
            "future" => samples[0] with { LastEvaluationUtc = GameHostWarningFixture.Now.AddSeconds(1) },
            _ => samples[0]
        };
        if (string.Equals(kind, "missing", StringComparison.Ordinal))
        {
            samples.RemoveAt(0);
        }

        var result = await ReadAsync(samples);
        result.Warnings!.State.Should().Be("Unknown");
        result.Warnings.Items.Should().BeEmpty();
        JsonSerializer.Serialize(result).Should().NotContain("private-label-sentinel");
        result.Values.Should().ContainSingle(static value => value.Metric == "swapUsedBytes" && value.Value == 0);
    }

    [Theory]
    [InlineData(-60, "Available")]
    [InlineData(-61, "Stale")]
    public async Task Old_rule_evaluation_is_not_current_even_with_a_successful_http_response(int seconds, string state)
    {
        var samples = GameHostWarningFixture.Fresh();
        samples[0] = samples[0] with { LastEvaluationUtc = GameHostWarningFixture.Now.AddSeconds(seconds), State = "firing" };
        var result = await ReadAsync(samples);
        result.Warnings!.State.Should().Be(state);
        result.Warnings.ObservedAtUtc.Should().Be(GameHostWarningFixture.Now.AddSeconds(seconds));
        result.Warnings.Items.Should().HaveCount(seconds == -60 ? 1 : 0);
    }

    [Fact]
    public async Task Failed_warning_transport_does_not_hide_other_measurements()
    {
        var result = await ReadAsync(samples: null);
        result.Warnings!.State.Should().Be("Unknown");
        result.Values.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_known_rule_can_be_displayed_without_truncation()
    {
        var samples = GameHostWarningFixture.Fresh().Select(static value => value with { State = "firing" }).ToList();
        var result = await ReadAsync(samples);
        result.Warnings!.State.Should().Be("Available");
        result.Warnings.Items.Should().HaveCount(17).And.OnlyContain(static value => value.State == "Firing" && value.Description.Length > 0);
    }

    [Fact]
    public void Warning_scope_matches_the_canonical_production_rule_names()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GoldSrcOps.sln")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();
        const string prefix = "      - alert: ";
        string[] files = ["game-event-agent.yml", "player-data-backup.yml", "game-host-storage.yml", "game-host-resources.yml"];
        var names = files.SelectMany(file => File.ReadLines(Path.Combine(root.FullName, "ops", "production", "observability", "rules", file)))
            .Where(static line => line.StartsWith(prefix, StringComparison.Ordinal)).Select(static line => line[prefix.Length..].Trim()).ToArray();
        GameHostWarningFixture.Fresh().Select(static value => value.Name).Should().BeEquivalentTo(names);
    }

    private static Task<GameHostHealthDto> ReadAsync(IReadOnlyList<GameHostWarningSample>? samples)
    {
        var source = new Mock<IGameHostHealthSource>();
        source.Setup(service => service.ReadAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new GameHostHealthSample(19, string.Empty, 0, GameHostWarningFixture.Now)]);
        var warnings = new Mock<IGameHostWarningSource>();
        warnings.Setup(service => service.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(samples);
        var clock = new Mock<IClock>();
        clock.SetupGet(static value => value.UtcNow).Returns(GameHostWarningFixture.Now);
        return new GameHostHealthService(source.Object, warnings.Object, clock.Object).ReadAsync(CancellationToken.None);
    }
}
