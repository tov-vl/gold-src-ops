using AwesomeAssertions;
using GoldSrcOps.Infrastructure.GameEvents;
using Microsoft.Extensions.Configuration;

namespace GoldSrcOps.UnitTests.GameEvents;

public sealed class GameEventRetentionOptionsTests
{
    [Fact]
    public void FromConfiguration_uses_bounded_defaults_when_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var result = GameEventRetentionOptions.FromConfiguration(configuration);

        result.Enabled.Should().BeTrue();
        result.RetentionPeriod.Should().Be(TimeSpan.FromDays(45));
        result.CleanupInterval.Should().Be(TimeSpan.FromMinutes(5));
        result.BatchSize.Should().Be(1_000);
    }

    [Fact]
    public void FromConfiguration_reads_valid_values()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GameEventRetention:Enabled"] = "false",
            ["GameEventRetention:RetentionDays"] = "90",
            ["GameEventRetention:CleanupIntervalSeconds"] = "600",
            ["GameEventRetention:BatchSize"] = "2500"
        });

        var result = GameEventRetentionOptions.FromConfiguration(configuration);

        result.Enabled.Should().BeFalse();
        result.RetentionPeriod.Should().Be(TimeSpan.FromDays(90));
        result.CleanupInterval.Should().Be(TimeSpan.FromMinutes(10));
        result.BatchSize.Should().Be(2_500);
    }

    [Theory]
    [InlineData("Enabled", "sometimes")]
    [InlineData("RetentionDays", "0")]
    [InlineData("RetentionDays", "3651")]
    [InlineData("CleanupIntervalSeconds", "9")]
    [InlineData("CleanupIntervalSeconds", "86401")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "10001")]
    public void FromConfiguration_rejects_invalid_values(string key, string value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"GameEventRetention:{key}"] = value
        });

        var act = () => GameEventRetentionOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*GameEventRetention:{key}*");
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
